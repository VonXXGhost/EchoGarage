using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Conversion;
using NAudio.Wave;
using System.Diagnostics;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 基于 NAudio 解码的响度分析器（ADR-028 §3.3）：把文件解码成 float32 后按约 8192 帧一块交给
/// <see cref="LoudnessMeter"/> 累计（ITU-R BS.1770-5 / EBU R128，算法在 Core 中实现）。
/// 分析在后台线程执行，支持进度回报与逐块取消；只读取音频数据，不写标签、数据库或缓存。
/// </summary>
public sealed class LoudnessAnalyzer : ILoudnessAnalyzer
{
    /// <summary>每次交给响度计的帧数。</summary>
    private const int BlockFrames = 8192;

    /// <summary>环绕声道能量权重（EBU R128 对环绕声道的 1.41 倍增益）。</summary>
    private const double SurroundWeight = 1.41;

    /// <summary>LFE 声道权重（BS.1770 不计入 LFE 能量）。</summary>
    private const double LfeWeight = 0.0;

    /// <summary>进度回报间隔：100 ms（10 Hz），高于 ADR-028 要求的每秒至少 5 次。</summary>
    private static readonly long ProgressIntervalTicks = Math.Max(1, Stopwatch.Frequency / 10);

    private const string StageDecoding = "解码";
    private const string StageAnalyzing = "分析";
    private const string StageCompleted = "完成";

    private readonly IAppLogger _logger;

    /// <summary>创建响度分析器。</summary>
    /// <param name="logger">用于记录解码失败、未知声道布局等诊断信息的日志器。</param>
    public LoudnessAnalyzer(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LoudnessMeasurement> AnalyzeAsync(
        string path,
        IProgress<LoudnessAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // 解码/滤波/真峰值都是 CPU 密集操作，统一切到线程池，避免阻塞 UI 线程。
        return await Task.Run(
            () => AnalyzeCore(path, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private LoudnessMeasurement AnalyzeCore(
        string path,
        IProgress<LoudnessAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new LoudnessAnalysisException(path, $"音频文件不存在：{path}");
        }

        DecodedAudio decoded;
        progress?.Report(new LoudnessAnalysisProgress(0.0, TimeSpan.Zero, null, StageDecoding));
        try
        {
            decoded = MediaDecoder.Open(path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warn($"响度分析无法解码文件：{path}；{exception.Message}");
            throw new LoudnessAnalysisException(
                path,
                $"无法解码音频文件“{Path.GetFileName(path)}”：{exception.Message}",
                exception);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = decoded.Provider.WaveFormat;
            var channels = format.Channels;
            var sampleRate = format.SampleRate;
            if (channels <= 0 || sampleRate <= 0)
            {
                throw new LoudnessAnalysisException(
                    path,
                    $"音频文件“{Path.GetFileName(path)}”的声道数或采样率无效（{channels} ch / {sampleRate} Hz）。");
            }

            var duration = decoded.Duration;
            var weights = ResolveChannelWeights(channels, TryGetContainerChannelMask(decoded), path);
            var meter = new LoudnessMeter(sampleRate, weights);

            ReportProgress(progress, 0.0, TimeSpan.Zero, duration, StageAnalyzing);

            var buffer = new float[BlockFrames * channels];
            var processedFrames = 0L;
            var lastReportTimestamp = Stopwatch.GetTimestamp();
            int read;
            while ((read = decoded.Provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frames = read / channels;
                if (frames > 0)
                {
                    meter.Process(buffer.AsSpan(0, frames * channels), frames);
                    processedFrames += frames;
                }

                var now = Stopwatch.GetTimestamp();
                if (now - lastReportTimestamp >= ProgressIntervalTicks)
                {
                    lastReportTimestamp = now;
                    ReportProgress(
                        progress,
                        ComputeFraction(processedFrames, sampleRate, duration),
                        FramesToDuration(processedFrames, sampleRate),
                        duration,
                        StageAnalyzing);
                }
            }

            var processedDuration = FramesToDuration(processedFrames, sampleRate);
            var measurement = meter.Complete(duration ?? processedDuration);
            ReportProgress(
                progress,
                duration is { } total && total > TimeSpan.Zero ? 1.0 : -1.0,
                processedDuration,
                duration,
                StageCompleted);

            _logger.Debug(
                $"响度分析完成：{path}；I={measurement.IntegratedLufs?.ToString("0.0") ?? "null"} LUFS，" +
                $"LRA={measurement.LoudnessRange?.ToString("0.0") ?? "null"} LU，TP={measurement.TruePeakDb:0.0} dBTP。");
            return measurement;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LoudnessAnalysisException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Warn($"响度分析失败：{path}；{exception.Message}");
            throw new LoudnessAnalysisException(
                path,
                $"分析音频文件“{Path.GetFileName(path)}”失败：{exception.Message}",
                exception);
        }
        finally
        {
            decoded.Owner.Dispose();
        }
    }

    private static void ReportProgress(
        IProgress<LoudnessAnalysisProgress>? progress,
        double fraction,
        TimeSpan processed,
        TimeSpan? duration,
        string stage)
    {
        progress?.Report(new LoudnessAnalysisProgress(fraction, processed, duration, stage));
    }

    private static double ComputeFraction(long processedFrames, int sampleRate, TimeSpan? duration)
    {
        if (duration is not { } total || total <= TimeSpan.Zero || sampleRate <= 0)
        {
            return -1.0;
        }

        var fraction = (processedFrames / (double)sampleRate) / total.TotalSeconds;
        return Math.Clamp(fraction, 0.0, 1.0);
    }

    private static TimeSpan FramesToDuration(long frames, int sampleRate) =>
        sampleRate > 0 ? TimeSpan.FromSeconds(frames / (double)sampleRate) : TimeSpan.Zero;

    /// <summary>
    /// 读取容器里的声道掩码：WAV 走 <see cref="WaveFileReader"/> 的原始 WaveFormat（EXTENSIBLE
    /// 的 dwChannelMask 在 NAudio 中以 WaveFormatExtraData 暴露），其余格式交给解码器输出的格式。
    /// </summary>
    private static int? TryGetContainerChannelMask(DecodedAudio decoded)
    {
        if (decoded.Owner is WaveFileReader waveReader)
        {
            var mask = MediaDecoder.TryGetChannelMask(waveReader.WaveFormat);
            if (mask is > 0)
            {
                return mask;
            }
        }

        return MediaDecoder.TryGetChannelMask(decoded.Provider.WaveFormat);
    }

    /// <summary>
    /// 解析每个声道的响度权重：有 WAVE_FORMAT_EXTENSIBLE 声道掩码时按掩码位顺序映射
    /// （WAV 的声道顺序即掩码位升序），否则按声道数回退到常见布局。
    /// </summary>
    private double[] ResolveChannelWeights(int channels, int? channelMask, string path)
    {
        if (channelMask is > 0)
        {
            var weights = new List<double>(channels);
            for (var bit = 0; bit < 32 && weights.Count < channels; bit++)
            {
                if ((channelMask.Value & (1 << bit)) != 0)
                {
                    weights.Add(WeightForSpeakerBit(bit));
                }
            }

            if (weights.Count == channels)
            {
                return weights.ToArray();
            }

            _logger.Warn(
                $"响度分析：{path} 的声道掩码 0x{channelMask.Value:X} 与声道数 {channels} 不一致，回退为按声道数推断布局。");
        }

        return FallbackWeights(channels, path);
    }

    /// <summary>
    /// 按 WAVEFORMATEXTENSIBLE 的 KSAUDIO_SPEAKER_* 位定义给声道权重：
    /// LFE（bit 3）= 0，Back/BackCenter/Side（bit 4、5、8、9、10）= 1.41，其它 = 1.0。
    /// </summary>
    private static double WeightForSpeakerBit(int bit) => bit switch
    {
        3 => LfeWeight,
        4 or 5 or 8 or 9 or 10 => SurroundWeight,
        _ => 1.0,
    };

    private double[] FallbackWeights(int channels, string path) => channels switch
    {
        1 => [1.0],
        2 => [1.0, 1.0],
        4 => [1.0, 1.0, SurroundWeight, SurroundWeight],
        5 => [1.0, 1.0, 1.0, SurroundWeight, SurroundWeight],
        6 => [1.0, 1.0, 1.0, LfeWeight, SurroundWeight, SurroundWeight],
        8 => [1.0, 1.0, 1.0, LfeWeight, SurroundWeight, SurroundWeight, SurroundWeight, SurroundWeight],
        _ => AllOnes(channels, path),
    };

    private double[] AllOnes(int channels, string path)
    {
        _logger.Warn($"响度分析：无法识别 {path} 的 {channels} 声道布局，所有声道按权重 1.0 计算。");
        var weights = new double[channels];
        Array.Fill(weights, 1.0);
        return weights;
    }
}
