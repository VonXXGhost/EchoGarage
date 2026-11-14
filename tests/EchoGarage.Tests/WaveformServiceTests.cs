using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Caching;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-015 §7.4 波形服务：缓存优先、单飞 / 同路径合并、负缓存、失败降级、日志。
/// </summary>
public sealed class WaveformServiceTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly RecordingLogger _logger = new();

    public void Dispose() => _workspace.Dispose();

    // ---- 缓存命中 ----

    [Fact]
    public async Task CacheHit_ReturnsSnapshotWithoutAnalysisOrProgress()
    {
        var wav = _workspace.Combine("hit.wav");
        var peaks = CreatePeaks(columnCount: 20);
        var cache = new FakeWaveformCache { LoadResult = peaks };
        var analyzer = new FakeAnalyzer();
        var progress = new SyncProgress<WaveformPeaks>();
        var service = new WaveformService(analyzer, cache, _logger);

        var result = await service.GetAsync(wav, progress);

        Assert.Same(peaks, result);
        Assert.Equal(0, analyzer.Calls);
        Assert.Empty(progress.Items);
        Assert.Equal(0, cache.SaveCalls);
    }

    [Fact]
    public async Task CacheHit_OnRealCache_SkipsAnalyzer()
    {
        var wav = _workspace.Combine("real-hit.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 0.5, frequency: 440);
        var cache = new FileWaveformCache(NewCacheDirectory(), _logger);
        await cache.SaveAsync(wav, CreatePeaks(columnCount: 20));
        var analyzer = new FakeAnalyzer();
        var progress = new SyncProgress<WaveformPeaks>();
        var service = new WaveformService(analyzer, cache, _logger);

        var result = await service.GetAsync(wav, progress);

        Assert.NotNull(result);
        Assert.Equal(20, result.ColumnCount);
        Assert.Equal(0, analyzer.Calls);
        Assert.Empty(progress.Items);
    }

    // ---- 未命中：分析 + 写缓存 ----

    [Fact]
    public async Task CacheMiss_AnalyzesAndSavesCompleteSnapshot()
    {
        var wav = _workspace.Combine("miss.wav");
        var peaks = CreatePeaks(columnCount: 30);
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer { Result = peaks };
        var service = new WaveformService(analyzer, cache, _logger);

        var result = await service.GetAsync(wav);

        Assert.Same(peaks, result);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, cache.SaveCalls);
        Assert.Equal(peaks, cache.SavedPeaks);
        Assert.Contains(_logger.Debugs, message => message.Contains("Waveform analyzed") && message.Contains("columns=30"));
    }

    [Fact]
    public async Task IncompleteSnapshot_IsNotWrittenToCache()
    {
        var wav = _workspace.Combine("incomplete.wav");
        var incomplete = CreatePeaks(columnCount: 30).WithValidCount(10);
        var cache = new FakeWaveformCache();
        var service = new WaveformService(new FakeAnalyzer { Result = incomplete }, cache, _logger);

        var result = await service.GetAsync(wav);

        Assert.Same(incomplete, result);
        Assert.Equal(0, cache.SaveCalls);
    }

    [Fact]
    public async Task SecondCacheCheck_AfterQueueing_ReturnsCachedResultWithoutAnalysis()
    {
        // 第一次查缓存 miss，排队拿到单飞锁后再查命中（别的路径已经算完写入）。
        var wav = _workspace.Combine("double-check.wav");
        var cached = CreatePeaks(columnCount: 12);
        var cache = new FakeWaveformCache { LoadResults = [null, cached] };
        var analyzer = new FakeAnalyzer();
        var service = new WaveformService(analyzer, cache, _logger);

        var result = await service.GetAsync(wav);

        Assert.Same(cached, result);
        Assert.Equal(0, analyzer.Calls);
    }

    // ---- 失败 / 负缓存 ----

    [Fact]
    public async Task AnalysisFailure_ReturnsNullAndNegativeCaches()
    {
        var wav = _workspace.Combine("failure.wav");
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer { Exception = new InvalidOperationException("decode boom") };
        var service = new WaveformService(analyzer, cache, _logger);

        Assert.Null(await service.GetAsync(wav));
        Assert.Null(await service.GetAsync(wav));

        Assert.Equal(1, analyzer.Calls); // 第二次命中负缓存，不再重试
        Assert.Equal(0, cache.SaveCalls);
        Assert.Contains(_logger.Warnings, message => message.Contains("Waveform analysis failed") && message.Contains("decode boom"));
    }

    [Fact]
    public async Task Invalidate_ClearsNegativeCacheAndAllowsRetry()
    {
        var wav = _workspace.Combine("retry.wav");
        var peaks = CreatePeaks(columnCount: 15);
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer { Exception = new InvalidOperationException("boom") };
        var service = new WaveformService(analyzer, cache, _logger);
        Assert.Null(await service.GetAsync(wav));

        service.Invalidate(wav);
        analyzer.Exception = null;
        analyzer.Result = peaks;
        var result = await service.GetAsync(wav);

        Assert.Same(peaks, result);
        Assert.Equal(2, analyzer.Calls);
        Assert.Equal(1, cache.InvalidateCalls);
    }

    [Fact]
    public async Task Cancellation_DoesNotNegativeCache()
    {
        var wav = _workspace.Combine("cancel.wav");
        var peaks = CreatePeaks(columnCount: 8);
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer { Exception = new OperationCanceledException() };
        var service = new WaveformService(analyzer, cache, _logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetAsync(wav));

        analyzer.Exception = null;
        analyzer.Result = peaks;
        Assert.Same(peaks, await service.GetAsync(wav));
        Assert.Equal(2, analyzer.Calls);
        Assert.DoesNotContain(_logger.Warnings, message => message.Contains("Waveform analysis failed"));
    }

    [Fact]
    public async Task CacheSaveFailure_DoesNotAffectResult()
    {
        var wav = _workspace.Combine("save-error.wav");
        var peaks = CreatePeaks(columnCount: 5);
        var cache = new FakeWaveformCache { SaveException = new IOException("disk full") };
        var service = new WaveformService(new FakeAnalyzer { Result = peaks }, cache, _logger);

        var result = await service.GetAsync(wav);

        Assert.Same(peaks, result);
        Assert.Contains(_logger.Warnings, message => message.Contains("Waveform cache write failed"));
    }

    // ---- 并发 ----

    [Fact]
    public async Task ConcurrentRequestsForSamePath_AreMerged()
    {
        var wav = _workspace.Combine("merged.wav");
        var peaks = CreatePeaks(columnCount: 25);
        var cache = new FakeWaveformCache();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var analyzer = new FakeAnalyzer
        {
            Handler = async (_, _, _) =>
            {
                started.TrySetResult();
                await release.Task;
                return peaks;
            },
        };
        var service = new WaveformService(analyzer, cache, _logger);

        var first = service.GetAsync(wav);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = service.GetAsync(wav);
        await Task.Delay(50);

        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(0, cache.SaveCalls);

        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Same(results[0], results[1]);
        Assert.Equal(1, analyzer.Calls);
        Assert.Equal(1, cache.SaveCalls);
    }

    [Fact]
    public async Task DifferentPaths_AreSerializedByGlobalGate()
    {
        var firstWav = _workspace.Combine("serial-a.wav");
        var secondWav = _workspace.Combine("serial-b.wav");
        var peaks = CreatePeaks(columnCount: 10);
        var cache = new FakeWaveformCache();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxActive = 0;
        var analyzer = new FakeAnalyzer
        {
            Handler = async (path, _, _) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, nowActive);
                if (path.EndsWith("serial-a.wav", StringComparison.OrdinalIgnoreCase))
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                Interlocked.Decrement(ref active);
                return peaks;
            },
        };
        var service = new WaveformService(analyzer, cache, _logger);

        var first = service.GetAsync(firstWav);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = service.GetAsync(secondWav);
        await Task.Delay(50);

        Assert.Equal(1, analyzer.Calls); // 第二首还没开始

        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, analyzer.Calls);
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task CancelledInFlightAnalysis_IsRetriedByLaterCaller()
    {
        // 快速切歌或重复播放同一首时，前一次请求会取消自己启动的共享任务；
        // 后一次请求仍然有效，必须重试而不是被连带取消（否则波形永远为空）。
        var wav = _workspace.Combine("retry.wav");
        var cache = new FakeWaveformCache();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var analyzer = new FakeAnalyzer
        {
            Handler = async (_, _, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    firstStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return CreatePeaks(columnCount: 12);
            },
        };
        var service = new WaveformService(analyzer, cache, _logger);

        using var firstCts = new CancellationTokenSource();
        var first = service.GetAsync(wav, null, firstCts.Token);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var result = await service.GetAsync(wav).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result);
        Assert.Equal(12, result!.ColumnCount);
        Assert.Equal(2, analyzer.Calls);
    }

    // ---- 日志 / 参数 ----

    [Fact]
    public async Task SlowAnalysis_LogsWarning()
    {
        var wav = _workspace.Combine("slow.wav");
        var peaks = CreatePeaks(columnCount: 5);
        var analyzer = new FakeAnalyzer
        {
            Handler = async (_, _, _) =>
            {
                await Task.Delay(30);
                return peaks;
            },
        };
        var service = new WaveformService(analyzer, new FakeWaveformCache(), _logger, TimeSpan.FromMilliseconds(1));

        Assert.NotNull(await service.GetAsync(wav));

        Assert.Contains(_logger.Warnings, message => message.Contains("Waveform analysis slow"));
    }

    [Fact]
    public async Task BlankPath_ReturnsNullWithoutTouchingDependencies()
    {
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer();
        var service = new WaveformService(analyzer, cache, _logger);

        Assert.Null(await service.GetAsync("  "));
        Assert.Null(await service.GetAsync(string.Empty));
        Assert.Equal(0, analyzer.Calls);
        Assert.Equal(0, cache.LoadCalls);
    }

    // ---- 清空 / 迁移 ----

    [Fact]
    public async Task ClearAsync_ForwardsToCacheAndClearsNegativeCache()
    {
        var wav = _workspace.Combine("clear.wav");
        var peaks = CreatePeaks(columnCount: 5);
        var cache = new FakeWaveformCache();
        var analyzer = new FakeAnalyzer { Exception = new InvalidOperationException("boom") };
        var service = new WaveformService(analyzer, cache, _logger);
        Assert.Null(await service.GetAsync(wav));

        await service.ClearAsync();
        analyzer.Exception = null;
        analyzer.Result = peaks;

        Assert.Same(peaks, await service.GetAsync(wav));
        Assert.Equal(1, cache.ClearCalls);
        Assert.Equal(2, analyzer.Calls);
    }

    [Fact]
    public async Task RelocateAsync_ForwardsNewRoot()
    {
        var cache = new FakeWaveformCache();
        var service = new WaveformService(new FakeAnalyzer(), cache, _logger);

        await service.RelocateAsync(@"D:\Other\Waveforms");
        Assert.Equal(@"D:\Other\Waveforms", cache.RelocatedTo);

        await service.RelocateAsync("   ");
        Assert.Equal(@"D:\Other\Waveforms", cache.RelocatedTo); // 空白路径不发转发
    }

    // ---- 集成：真实分析器 + 真实缓存 ----

    [Fact]
    public async Task RealAnalyzerAndCache_SecondRequestHitsCache()
    {
        var wav = _workspace.Combine("integration.wav");
        WaveformTestAudio.WriteShapedSine(wav, seconds: 2.0);
        var cache = new FileWaveformCache(NewCacheDirectory(), _logger);
        var service = new WaveformService(new WaveformAnalyzer(), cache, _logger);
        var firstProgress = new SyncProgress<WaveformPeaks>();

        var first = await service.GetAsync(wav, firstProgress);
        var second = await service.GetAsync(wav);

        Assert.NotNull(first);
        Assert.True(first.IsComplete);
        Assert.NotEmpty(firstProgress.Items);
        Assert.NotNull(second);
        Assert.Equal(first.ColumnCount, second.ColumnCount);
        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Low, second.Low);
        Assert.Single(Directory.GetFiles(cache.Directory, "*.wfm"));
    }

    // ---- 辅助 ----

    private string NewCacheDirectory() => _workspace.Combine("Waveforms");

    private static WaveformPeaks CreatePeaks(int columnCount = 100) => new()
    {
        ColumnsPerSecond = WaveformMath.ColumnsPerSecond,
        ColumnCount = columnCount,
        ValidCount = columnCount,
        Duration = TimeSpan.FromSeconds(2),
        SampleRate = 44100,
        FloorDb = -40,
        CeilingDb = -20,
        Height = Enumerable.Repeat((byte)128, columnCount).ToArray(),
        Low = Enumerable.Repeat((byte)10, columnCount).ToArray(),
        Mid = Enumerable.Repeat((byte)200, columnCount).ToArray(),
        High = Enumerable.Repeat((byte)45, columnCount).ToArray(),
    };

    /// <summary>可编程的分析器替身。</summary>
    private sealed class FakeAnalyzer : IWaveformAnalyzer
    {
        private int _calls;

        public Func<string, IProgress<WaveformPeaks>?, CancellationToken, Task<WaveformPeaks>>? Handler { get; set; }

        public WaveformPeaks Result { get; set; } = CreatePeaks(4);

        public Exception? Exception { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int Version => 1;

        public Task<WaveformPeaks> AnalyzeAsync(
            string filePath,
            IProgress<WaveformPeaks>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            var exception = Exception;
            if (exception is not null)
            {
                return Task.FromException<WaveformPeaks>(exception);
            }

            return Handler is not null
                ? Handler(filePath, progress, cancellationToken)
                : Task.FromResult(Result);
        }
    }

    /// <summary>可编程的缓存替身。</summary>
    private sealed class FakeWaveformCache : IWaveformCache
    {
        private readonly Queue<WaveformPeaks?> _loadResults = new();
        private int _loadCalls;
        private int _saveCalls;
        private int _clearCalls;
        private int _invalidateCalls;

        public WaveformPeaks? LoadResult { get; set; }

        public WaveformPeaks?[]? LoadResults { get; set; }

        public WaveformPeaks? SavedPeaks { get; private set; }

        public Exception? SaveException { get; set; }

        public string? RelocatedTo { get; private set; }

        public int LoadCalls => Volatile.Read(ref _loadCalls);

        public int SaveCalls => Volatile.Read(ref _saveCalls);

        public int ClearCalls => Volatile.Read(ref _clearCalls);

        public int InvalidateCalls => Volatile.Read(ref _invalidateCalls);

        public string Directory => "fake-waveforms";

        public Task<WaveformPeaks?> TryLoadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _loadCalls);
            if (LoadResults is { Length: > 0 })
            {
                if (_loadResults.Count == 0)
                {
                    foreach (var value in LoadResults)
                    {
                        _loadResults.Enqueue(value);
                    }
                }

                return Task.FromResult(_loadResults.Dequeue());
            }

            return Task.FromResult(LoadResult);
        }

        public Task SaveAsync(string filePath, WaveformPeaks peaks, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCalls);
            var exception = SaveException;
            if (exception is not null)
            {
                return Task.FromException(exception);
            }

            SavedPeaks = peaks;
            return Task.CompletedTask;
        }

        public void Invalidate(string filePath) => Interlocked.Increment(ref _invalidateCalls);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _clearCalls);
            return Task.CompletedTask;
        }

        public Task RelocateAsync(string newRoot, CancellationToken cancellationToken = default)
        {
            RelocatedTo = newRoot;
            return Task.CompletedTask;
        }

        public Task<int> TrimAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
