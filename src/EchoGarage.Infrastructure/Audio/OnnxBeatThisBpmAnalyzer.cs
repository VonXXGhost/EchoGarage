using System.Diagnostics;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// Beat This!（ISMIR 2024）ONNX 离线 BPM 分析器（ADR-034）：
/// <c>SharedAudioFileReader</c> 共享句柄解码 → 下混单声道并重采样至 22,050 Hz →
/// 纯 C# 128-bin Slaney Log-Mel → 固定 1500 帧（30 s）重叠分块 DirectML/CPU 推理 →
/// 无 DBN 拍点拾取与核密度/截尾均值全局 BPM 聚合。
/// </summary>
public sealed class OnnxBeatThisBpmAnalyzer : IBpmAnalyzer
{
    public const int AnalyzerVersion = 1;
    private const int ChunkFrames = OnnxBeatThisSessionHost.ChunkFrames; // 1500 (30 s)
    private const int BorderFrames = 150;                                // 3.0 s overlap per side
    private const int StepFrames = ChunkFrames - 2 * BorderFrames;       // 1200 (24 s)
    private const int ReadBlockFrames = 16384;

    private readonly BpmModelLocator _locator;
    private readonly Func<BpmSettings> _settingsProvider;
    private readonly IAppLogger _logger;
    private readonly OnnxBeatThisSessionHost _sessionHost;

    public OnnxBeatThisBpmAnalyzer(
        BpmModelLocator locator,
        Func<BpmSettings> settingsProvider,
        IAppLogger logger)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionHost = new OnnxBeatThisSessionHost(logger);
    }

    public string AlgorithmId
    {
        get
        {
            var settings = _settingsProvider();
            var status = _locator.GetStatus(settings.PreferredModel);
            return FormatAlgorithmId(status.EffectiveModel);
        }
    }

    public int Version => AnalyzerVersion;

    internal bool IsRuntimeLoaded => _sessionHost.IsLoaded;

    public BpmModelStatus GetModelStatus() => _locator.GetStatus(_settingsProvider().PreferredModel);

    public void UnloadRuntime() => _sessionHost.Unload();

    public void Dispose() => _sessionHost.Dispose();

    public Task<BpmAnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return Task.Run(async () =>
        {
            var previousPriority = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                return await AnalyzeCoreAsync(filePath, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    Thread.CurrentThread.Priority = previousPriority;
                }
                catch
                {
                    // Ignore thread priority restore failures.
                }
            }
        }, cancellationToken);
    }

    private async Task<BpmAnalysisResult> AnalyzeCoreAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new BpmAnalysisException(filePath, $"音频文件不存在：{filePath}");
        }

        var settings = BpmSettings.Normalize(_settingsProvider());
        var resolved = _locator.ResolveForInference(settings.PreferredModel);
        if (resolved is null)
        {
            throw new BpmAnalysisException(
                filePath,
                $"未找到 BPM 分析模型文件（{BpmFormatter.Small0FileName}），请确认程序目录下存在 tools\\models\\{BpmFormatter.Small0FileName}。");
        }

        var (actualKind, modelPath) = resolved.Value;
        var algorithmId = FormatAlgorithmId(actualKind);
        var stopwatch = Stopwatch.StartNew();

        // 1. 共享句柄解码 → 单声道 22,050 Hz
        progress?.Report(new BpmAnalysisProgress(0.02, "解码音频"));
        float[] mono22k;
        try
        {
            mono22k = await DecodeToMono22kAsync(filePath, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"BPM decode failed for {filePath}: {ex.Message}");
            throw new BpmAnalysisException(filePath, $"无法解码音频文件“{Path.GetFileName(filePath)}”：{ex.Message}", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (mono22k.Length < MelSpectrogramMath.SampleRate * 3)
        {
            stopwatch.Stop();
            return new BpmAnalysisResult(
                0, 0, [], false, algorithmId, AnalyzerVersion, _sessionHost.CurrentExecutionProvider,
                stopwatch.Elapsed, IsReliable: false, Note: "音频时长短于 3 秒，无法可靠测量 BPM。");
        }

        // 2. 提取 128-bin Log-Mel 频谱图（50 fps）
        progress?.Report(new BpmAnalysisProgress(0.28, "提取频谱特征"));
        var (melData, frameCount) = MelSpectrogramMath.Compute(mono22k);
        // 尽早释放整曲单声道数组，降低峰值内存。
        mono22k = [];
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();

        // 3. 固定 1500 帧（30 s）重叠分块 ONNX 推理
        progress?.Report(new BpmAnalysisProgress(0.42, _sessionHost.IsLoaded ? "分析节拍" : "初始化推理引擎"));
        var allBeatLogits = new float[frameCount];
        var chunkBuffer = new float[ChunkFrames * MelSpectrogramMath.NumMels];
        var executionProvider = "CPU";

        try
        {
            var chunkStarts = BuildChunkStarts(frameCount);
            for (var c = 0; c < chunkStarts.Count; c++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startFrame = chunkStarts[c];
                var validFrames = Math.Min(ChunkFrames, frameCount - startFrame);

                var srcFloatOffset = startFrame * MelSpectrogramMath.NumMels;
                var validFloats = validFrames * MelSpectrogramMath.NumMels;
                Array.Copy(melData, srcFloatOffset, chunkBuffer, 0, validFloats);
                if (validFloats < chunkBuffer.Length)
                {
                    Array.Clear(chunkBuffer, validFloats, chunkBuffer.Length - validFloats);
                }

                var (chunkLogits, ep) = _sessionHost.RunChunk(modelPath, settings.UseGpuAcceleration, chunkBuffer);
                executionProvider = ep;

                // 拼接有效无边界效应区段
                var copyFrom = c == 0 ? 0 : BorderFrames;
                var copyTo = c == chunkStarts.Count - 1
                    ? validFrames
                    : Math.Min(validFrames, ChunkFrames - BorderFrames);
                for (var f = copyFrom; f < copyTo; f++)
                {
                    var dst = startFrame + f;
                    if (dst < frameCount)
                    {
                        allBeatLogits[dst] = chunkLogits[f];
                    }
                }

                var fraction = 0.45 + 0.50 * (c + 1) / chunkStarts.Count;
                progress?.Report(new BpmAnalysisProgress(fraction, $"分析节拍（{executionProvider}）"));
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"BPM ONNX inference failed for {filePath} (model={modelPath}): {ex.Message}");
            throw new BpmAnalysisException(
                filePath,
                $"BPM 模型推理失败（{Path.GetFileName(modelPath)}）：{ex.Message}",
                ex);
        }
        finally
        {
            _sessionHost.NotifyAnalysisFinished(settings.IdleUnloadSeconds);
        }

        // 4. 拍点拾取与全局 BPM 聚合
        progress?.Report(new BpmAnalysisProgress(0.96, "计算节拍"));
        var estimation = BeatThisPostProcessor.EstimateGlobalBpm(allBeatLogits);
        stopwatch.Stop();

        var result = new BpmAnalysisResult(
            Bpm: estimation.Bpm,
            Confidence: estimation.Confidence,
            Candidates: estimation.Candidates,
            IsVariableTempoSuspected: estimation.IsVariableTempoSuspected,
            AlgorithmId: algorithmId,
            AlgorithmVersion: AnalyzerVersion,
            ExecutionProvider: executionProvider,
            Elapsed: stopwatch.Elapsed,
            IsReliable: estimation.IsReliable,
            Note: estimation.Note)
        {
            Segments = estimation.Segments,
            DetectedSegmentCount = estimation.DetectedSegmentCount,
            TooManyTempoChanges = estimation.TooManyTempoChanges
        };

        progress?.Report(new BpmAnalysisProgress(1.0, "完成"));
        _logger.Info(
            $"BPM analyzed: path={filePath}, model={algorithmId}, ep={executionProvider}, " +
            $"bpm={(result.IsReliable ? result.Bpm.ToString("0.0") : "unreliable")}, " +
            $"confidence={result.Confidence:0.00}, variable={result.IsVariableTempoSuspected}, " +
            $"segments={result.DetectedSegmentCount}{(result.TooManyTempoChanges ? " (too-many)" : string.Empty)}, " +
            $"peaks={estimation.PeakCount}, elapsedMs={stopwatch.ElapsedMilliseconds}.");
        return result;
    }

    private static List<int> BuildChunkStarts(int totalFrames)
    {
        var starts = new List<int> { 0 };
        if (totalFrames <= ChunkFrames)
        {
            return starts;
        }

        var next = StepFrames;
        while (next < totalFrames)
        {
            starts.Add(next);
            if (next + ChunkFrames >= totalFrames)
            {
                break;
            }

            next += StepFrames;
        }

        return starts;
    }

    private static async Task<float[]> DecodeToMono22kAsync(
        string filePath,
        IProgress<BpmAnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var reader = new SharedAudioFileReader(filePath);
        ISampleProvider monoProvider = reader.WaveFormat.Channels switch
        {
            1 => reader,
            2 => new StereoToMonoSampleProvider(reader) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => new MultiChannelToMonoProvider(reader)
        };

        ISampleProvider resampled = monoProvider.WaveFormat.SampleRate == MelSpectrogramMath.SampleRate
            ? monoProvider
            : new WdlResamplingSampleProvider(monoProvider, MelSpectrogramMath.SampleRate);

        var totalSeconds = reader.TotalTime.TotalSeconds;
        var estimatedSamples = totalSeconds > 0
            ? (int)Math.Clamp(totalSeconds * MelSpectrogramMath.SampleRate + 4096, 4096, 200_000_000)
            : MelSpectrogramMath.SampleRate * 180;

        var buffer = new float[ReadBlockFrames];
        var chunks = new List<float[]>(Math.Max(16, estimatedSamples / ReadBlockFrames + 1));
        var totalRead = 0L;
        var blocks = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = resampled.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var copy = new float[read];
            Array.Copy(buffer, 0, copy, 0, read);
            chunks.Add(copy);
            totalRead += read;
            blocks++;

            if (blocks % 8 == 0)
            {
                var frac = totalSeconds > 0
                    ? Math.Clamp((totalRead / (double)MelSpectrogramMath.SampleRate) / totalSeconds, 0.0, 1.0)
                    : 0.5;
                progress?.Report(new BpmAnalysisProgress(0.02 + 0.24 * frac, "解码音频"));
                await Task.Yield();
            }
        }

        var output = new float[totalRead];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, output, offset, chunk.Length);
            offset += chunk.Length;
        }

        return output;
    }

    private static string FormatAlgorithmId(BpmModelKind kind) => kind switch
    {
        BpmModelKind.Final0 => "beat-this-final0-onnx",
        _ => "beat-this-small0-onnx"
    };

    private sealed class MultiChannelToMonoProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public MultiChannelToMonoProvider(ISampleProvider source)
        {
            _source = source;
            _channels = Math.Max(1, source.WaveFormat.Channels);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_sourceBuffer.Length < needed)
            {
                _sourceBuffer = new float[needed];
            }

            var read = _source.Read(_sourceBuffer, 0, needed);
            var frames = read / _channels;
            var inv = 1.0f / _channels;
            for (var i = 0; i < frames; i++)
            {
                var sum = 0.0f;
                var baseIdx = i * _channels;
                for (var c = 0; c < _channels; c++)
                {
                    sum += _sourceBuffer[baseIdx + c];
                }

                buffer[offset + i] = sum * inv;
            }

            return frames;
        }
    }
}
