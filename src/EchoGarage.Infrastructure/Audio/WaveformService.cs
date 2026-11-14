using System.Collections.Concurrent;
using System.Diagnostics;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// ADR-015 §7.4 的波形服务：先查缓存（命中直接返回完整快照、不触发进度），未命中才在低优先级后台线程
/// 跑分析器；同一路径的并发请求合并成一个任务，全局同时只允许一个分析（单飞）；
/// 失败记 Warn 并做会话内负缓存，只有完整快照才写回缓存。
/// </summary>
public sealed class WaveformService : IWaveformService
{
    private static readonly TimeSpan DefaultSlowThreshold = TimeSpan.FromSeconds(3);

    private readonly IWaveformAnalyzer _analyzer;
    private readonly IWaveformCache _cache;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _slowThreshold;

    /// <summary>真正开始解码前等待的时间，让播放先抢到磁盘 / CPU（ADR-015 §6.10 建议 300–500 ms）。</summary>
    private readonly TimeSpan _analysisStartDelay;

    /// <summary>同一路径的并发请求共享同一个任务（避免重复解码同一首歌）。</summary>
    private readonly ConcurrentDictionary<string, Task<WaveformPeaks?>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>会话内负缓存：分析失败的文件在本次运行期间不再重试。</summary>
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>全局单飞：同一时刻只分析一首，避免多个解码线程抢磁盘 / CPU。</summary>
    private readonly SemaphoreSlim _analysisGate = new(1, 1);

    /// <param name="analyzer">波形分析器。</param>
    /// <param name="cache">波形缓存。</param>
    /// <param name="logger">日志。</param>
    /// <param name="slowThreshold">超过该耗时记一条 Warn（默认 3 s，测试可缩短）。</param>
    /// <param name="analysisStartDelay">开始解码前的等待时间（默认不等待；播放路径由 AppHost 传入 350 ms）。</param>
    public WaveformService(
        IWaveformAnalyzer analyzer,
        IWaveformCache cache,
        IAppLogger logger,
        TimeSpan? slowThreshold = null,
        TimeSpan? analysisStartDelay = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _slowThreshold = slowThreshold ?? DefaultSlowThreshold;
        _analysisStartDelay = analysisStartDelay ?? TimeSpan.Zero;
    }

    public async Task<WaveformPeaks?> GetAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var path = NormalizePath(filePath);
        if (_failed.ContainsKey(path))
        {
            return null;
        }

        // 缓存命中：直接返回完整快照，不触发进度（ADR-015 §7.4）。
        var cached = await Task.Run(() => _cache.TryLoadAsync(path, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // 同路径合并：只有把 TaskCompletionSource 放进字典的那个调用者真正启动分析。
        // 共享任务可能被它自己的调用者取消（快速切歌 / 重复播放同一首），此时本请求仍然有效，
        // 应短暂等待后重试，而不是把取消结果直接透传给调用者。
        for (var attempt = 0; ; attempt++)
        {
            var completion = new TaskCompletionSource<WaveformPeaks?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var shared = _inFlight.GetOrAdd(path, completion.Task);
            if (ReferenceEquals(shared, completion.Task))
            {
                return await RunSharedAnalysisAsync(path, progress, cancellationToken, completion).ConfigureAwait(false);
            }

            try
            {
                // 后来的调用者可以独立取消等待，但不会中断正在进行的分析。
                return await shared.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>启动一次共享分析，并把结果 / 异常 / 取消传播给所有等待者。</summary>
    private async Task<WaveformPeaks?> RunSharedAnalysisAsync(
        string path,
        IProgress<WaveformPeaks>? progress,
        CancellationToken cancellationToken,
        TaskCompletionSource<WaveformPeaks?> completion)
    {
        try
        {
            var result = await AnalyzeCoreAsync(path, progress, cancellationToken).ConfigureAwait(false);
            completion.TrySetResult(result);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            completion.TrySetCanceled(exception.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Task<WaveformPeaks?>>(path, completion.Task));
        }
    }

    public void Invalidate(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        var path = NormalizePath(filePath);
        _cache.Invalidate(path);
        _failed.TryRemove(path, out _);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _cache.ClearAsync(cancellationToken).ConfigureAwait(false);
        _failed.Clear();
    }

    public Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default)
    {
        return string.IsNullOrWhiteSpace(newRoot)
            ? Task.CompletedTask
            : _cache.RelocateAsync(newRoot, cancellationToken);
    }

    /// <summary>真正执行分析：全局单飞 → 二次查缓存 → 低优先级后台线程解码 → 记日志 → 写缓存。</summary>
    private async Task<WaveformPeaks?> AnalyzeCoreAsync(
        string path,
        IProgress<WaveformPeaks>? progress,
        CancellationToken cancellationToken)
    {
        await _analysisGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 排队期间可能已有别的路径把它算完并写进了缓存。
            var cached = await _cache.TryLoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (cached is not null)
            {
                return cached;
            }

            if (_analysisStartDelay > TimeSpan.Zero)
            {
                // 刚切歌时播放器正在解码 / 建缓冲，分析先让一让，避免抢磁盘。
                await Task.Delay(_analysisStartDelay, cancellationToken).ConfigureAwait(false);
            }

            var stopwatch = Stopwatch.StartNew();
            WaveformPeaks peaks;
            try
            {
                peaks = await Task.Factory.StartNew(
                        () => AnalyzeWithLowPriority(path, progress, cancellationToken),
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning,
                        TaskScheduler.Default)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户取消不算失败，不写负缓存。
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                _failed[path] = 0;
                _logger.Warn($"Waveform analysis failed path={path} ms={stopwatch.ElapsedMilliseconds} error={exception.Message}");
                return null;
            }

            stopwatch.Stop();
            _logger.Debug($"Waveform analyzed path={path} columns={peaks.ColumnCount} ms={stopwatch.ElapsedMilliseconds}");
            if (stopwatch.Elapsed > _slowThreshold)
            {
                _logger.Warn($"Waveform analysis slow path={path} ms={stopwatch.ElapsedMilliseconds} columns={peaks.ColumnCount}");
            }

            // 只有完成快照才写缓存（ADR-015 §6.10）；缓存写失败不影响本次结果。
            if (peaks.IsComplete)
            {
                try
                {
                    await _cache.SaveAsync(path, peaks, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.Warn($"Waveform cache write failed path={path} error={exception.Message}");
                }
            }

            return peaks;
        }
        finally
        {
            _analysisGate.Release();
        }
    }

    /// <summary>
    /// 在专用线程上以 BelowNormal 优先级执行分析器（ADR-015 §6.10）。
    /// 说明：分析器在渐进发布处会 await Task.Yield()，续体回到线程池后不再受该优先级约束，
    /// 但解码 / FFT 的同步主体仍跑在低优先级线程上。
    /// </summary>
    private WaveformPeaks AnalyzeWithLowPriority(
        string path,
        IProgress<WaveformPeaks>? progress,
        CancellationToken cancellationToken)
    {
        var previousPriority = Thread.CurrentThread.Priority;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            return _analyzer.AnalyzeAsync(path, progress, cancellationToken).GetAwaiter().GetResult();
        }
        finally
        {
            Thread.CurrentThread.Priority = previousPriority;
        }
    }

    /// <summary>统一路径形态，保证合并字典 / 缓存键 / 负缓存使用同一个字符串。</summary>
    private static string NormalizePath(string filePath)
    {
        var trimmed = filePath.Trim();
        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return trimmed;
        }
    }
}
