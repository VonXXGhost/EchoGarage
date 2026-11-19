using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;
using NAudio.Wave;

namespace EchoGarage.Tests;

/// <summary>
/// 响度分析器（<see cref="LoudnessAnalyzer"/>，ADR-028 §3.3）的测试：
/// 用真实 WAV 文件走完整解码链路，覆盖采样率/位深/声道布局、取消、进度、错误包装；
/// 有 ffmpeg 时与 <c>ebur128</c> 过滤器做端到端对照（缺少 ffmpeg 的机器跳过）。
/// </summary>
public sealed class LoudnessAnalyzerTests
{
    // K 加权在 1 kHz 处约 +0.7 dB：1 kHz、幅度 0.5 的单声道正弦积分响度约 -9.0 LUFS。
    private const double MonoHalfAmplitudeIntegrated = -9.0;
    private const double HalfAmplitudeTruePeak = -6.0;

    private static readonly Regex IntegratedRegex = new(@"I:\s*(-?\d+(?:\.\d+)?)\s*LUFS", RegexOptions.Compiled);
    private static readonly Regex LoudnessRangeRegex = new(@"LRA:\s*(-?\d+(?:\.\d+)?)\s*LU", RegexOptions.Compiled);
    private static readonly Regex TruePeakRegex = new(@"Peak:\s*(-?\d+(?:\.\d+)?)\s*dBFS", RegexOptions.Compiled);

    [Fact]
    public async Task Analyze_16BitStereoWav_ReportsLoudnessDurationAndChannels()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("stereo.wav"),
            channels: 2,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.5, 0.5]);

        var measurement = await analyzer.AnalyzeAsync(path);

        // 两个声道叠加同相信号，比单声道多 3 dB。
        Assert.InRange(measurement.IntegratedLufs!.Value, MonoHalfAmplitudeIntegrated + 3.0 - 0.5, MonoHalfAmplitudeIntegrated + 3.0 + 0.5);        Assert.InRange(measurement.TruePeakDb, HalfAmplitudeTruePeak - 0.3, HalfAmplitudeTruePeak + 0.3);
        Assert.InRange(measurement.SamplePeakDb, HalfAmplitudeTruePeak - 0.3, HalfAmplitudeTruePeak + 0.3);
        Assert.Equal(44100, measurement.SampleRate);
        Assert.Equal(2, measurement.Channels);
        Assert.InRange(measurement.Duration.TotalSeconds, 1.99, 2.01);
        Assert.Null(measurement.Note);
    }

    [Fact]
    public async Task Analyze_24BitWav_ReportsSameLoudnessAt48kHz()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("mono24.wav"),
            channels: 1,
            sampleRate: 48000,
            bitDepth: 24,
            seconds: 2,
            amplitudes: [0.5]);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.InRange(measurement.IntegratedLufs!.Value, MonoHalfAmplitudeIntegrated - 0.5, MonoHalfAmplitudeIntegrated + 0.5);
        Assert.InRange(measurement.TruePeakDb, HalfAmplitudeTruePeak - 0.2, HalfAmplitudeTruePeak + 0.2);
        Assert.Equal(48000, measurement.SampleRate);
    }

    [Fact]
    public async Task Analyze_96kHzWav_DesignsKWeightingForSampleRate()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("mono96k.wav"),
            channels: 1,
            sampleRate: 96000,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.5]);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.InRange(measurement.IntegratedLufs!.Value, MonoHalfAmplitudeIntegrated - 0.6, MonoHalfAmplitudeIntegrated + 0.6);
        Assert.Equal(96000, measurement.SampleRate);
    }

    [Fact]
    public async Task Analyze_ShortWav_ReturnsMeasurementWithNote()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("short.wav"),
            channels: 1,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 0.2,
            amplitudes: [0.5]);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.Null(measurement.IntegratedLufs);
        Assert.NotNull(measurement.Note);
        Assert.InRange(measurement.Duration.TotalSeconds, 0.19, 0.21);
    }

    [Fact]
    public async Task Analyze_SilentWav_ReturnsNullIntegratedAndInfinitePeaks()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("silence.wav"),
            channels: 2,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.0, 0.0]);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.Null(measurement.IntegratedLufs);
        Assert.True(double.IsNegativeInfinity(measurement.TruePeakDb));
        Assert.True(double.IsNegativeInfinity(measurement.SamplePeakDb));
        Assert.NotNull(measurement.Note);
    }

    [Fact]
    public async Task Analyze_MissingFile_ThrowsLoudnessAnalysisException()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var missing = workspace.Combine("missing.wav");

        var exception = await Assert.ThrowsAsync<LoudnessAnalysisException>(() => analyzer.AnalyzeAsync(missing));

        Assert.Equal(missing, exception.Path);
        Assert.Contains("不存在", exception.Message);
    }

    [Fact]
    public async Task Analyze_CorruptFile_ThrowsLoudnessAnalysisException()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var corrupt = workspace.Combine("corrupt.wav");
        File.WriteAllBytes(corrupt, "this is not a wave file at all"u8.ToArray());

        var exception = await Assert.ThrowsAsync<LoudnessAnalysisException>(() => analyzer.AnalyzeAsync(corrupt));

        Assert.Equal(corrupt, exception.Path);
        Assert.NotNull(exception.InnerException);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public async Task Analyze_AlreadyCanceledToken_ThrowsOperationCanceledException()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(workspace.Combine("cancel.wav"), seconds: 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(path, progress: null, cancellation.Token));
    }

    [Fact]
    public async Task Analyze_CancelDuringAnalysis_ThrowsOperationCanceledException()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(workspace.Combine("cancel-mid.wav"), seconds: 30);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(path, progress: null, cancellation.Token));
    }

    [Fact]
    public async Task Analyze_ReportsProgressFromStartToCompletion()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(workspace.Combine("progress.wav"), seconds: 3);
        var collector = new CollectingProgress();

        var measurement = await analyzer.AnalyzeAsync(path, collector);

        Assert.True(collector.Reports.Count >= 2, $"只收到 {collector.Reports.Count} 次进度回报。");
        Assert.Equal(0.0, collector.Reports[0].Fraction, 3);
        Assert.Equal(1.0, collector.Reports[^1].Fraction, 3);
        Assert.All(collector.Reports, report => Assert.False(string.IsNullOrWhiteSpace(report.Stage)));
        Assert.Equal(measurement.Duration, collector.Reports[^1].Duration);
        for (var index = 1; index < collector.Reports.Count; index++)
        {
            Assert.True(
                collector.Reports[index].Fraction >= collector.Reports[index - 1].Fraction - 1e-9,
                "进度分数出现了回退。");
        }
    }

    [Fact]
    public async Task Analyze_ReleasesFileHandleAndLeavesSourceUntouched()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        var path = TestPcmAudio.WriteSineWav(workspace.Combine("locked.wav"), seconds: 1);
        var before = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));

        await analyzer.AnalyzeAsync(path);

        // 分析器必须在返回前释放解码器句柄，否则 Windows 上不能移动/删除文件。
        var moved = workspace.Combine("moved.wav");
        File.Move(path, moved);
        Assert.Equal(before, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(moved)));
    }

    [Fact]
    public async Task Analyze_ExtensibleSixChannel_ExcludesLfeFromLoudness()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        // 5.1 布局：FL FR FC LFE BL BR；只在 LFE（第 4 声道）放 1kHz 正弦。
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("lfe.wav"),
            channels: 6,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.0, 0.0, 0.0, 0.5, 0.0, 0.0],
            channelMask: 0x3F);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.Null(measurement.IntegratedLufs);
        // 真峰值按原始 PCM 计算，LFE 信号仍然可见（不被 K 加权/权重影响）。
        Assert.InRange(measurement.TruePeakDb, HalfAmplitudeTruePeak - 0.3, HalfAmplitudeTruePeak + 0.3);
        Assert.Equal(6, measurement.Channels);
    }

    [Fact]
    public async Task Analyze_ExtensibleSixChannel_AppliesSurroundWeightFromChannelMask()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        // 5.1 side 布局：FL FR FC LFE SL SR（掩码 0x60F），分别只在 FL 与 SL 放同样信号。
        var front = TestPcmAudio.WriteSineWav(
            workspace.Combine("front.wav"),
            channels: 6,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.5, 0.0, 0.0, 0.0, 0.0, 0.0],
            channelMask: 0x60F);
        var side = TestPcmAudio.WriteSineWav(
            workspace.Combine("side.wav"),
            channels: 6,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.0, 0.0, 0.0, 0.0, 0.5, 0.0],
            channelMask: 0x60F);

        var frontMeasurement = await analyzer.AnalyzeAsync(front);
        var sideMeasurement = await analyzer.AnalyzeAsync(side);

        // 环绕声道权重 1.41 → 能量多 10*log10(1.41) ≈ 1.49 LU。
        Assert.NotNull(frontMeasurement.IntegratedLufs);
        Assert.NotNull(sideMeasurement.IntegratedLufs);
        Assert.InRange(sideMeasurement.IntegratedLufs!.Value - frontMeasurement.IntegratedLufs!.Value, 1.34, 1.64);
    }

    [Fact]
    public async Task Analyze_PlainSixChannelWav_FallsBackToLfeExclusion()
    {
        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        // 无 WAVE_FORMAT_EXTENSIBLE 头的 6 声道 WAV：按 L R C LFE Ls Rs 回退推断布局。
        var path = TestPcmAudio.WriteSineWav(
            workspace.Combine("plain-lfe.wav"),
            channels: 6,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.0, 0.0, 0.0, 0.5, 0.0, 0.0]);

        var measurement = await analyzer.AnalyzeAsync(path);

        Assert.Null(measurement.IntegratedLufs);
        Assert.NotNull(measurement.Note);
    }

    [Fact]
    public async Task Analyze_GeneratedSignal_MatchesFfmpegEbur128()
    {
        var ffmpeg = TryFindFfmpeg();
        if (ffmpeg is null)
        {
            Assert.Skip("未找到 ffmpeg.exe（可用 FFMPEG_PATH 环境变量或 PATH 指定），跳过端到端对照。");
        }

        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);
        // 20 秒动态信号：安静段 → 中段 → 响段 → 尾段，覆盖绝对/相对门限与 LRA。
        var path = TestPcmAudio.WriteEnvelopeWav(
            workspace.Combine("envelope.wav"),
            channels: 2,
            sampleRate: 44100,
            seconds: 20,
            amplitudeAt: time => time switch
            {
                < 6 => 0.02,
                < 12 => 0.15,
                < 18 => 0.5,
                _ => 0.005
            });

        var measurement = await analyzer.AnalyzeAsync(path);
        var reference = await RunFfmpegEbur128Async(ffmpeg, path);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, reference.Integrated - 0.3, reference.Integrated + 0.3);
        Assert.NotNull(measurement.LoudnessRange);
        Assert.InRange(measurement.LoudnessRange!.Value, reference.LoudnessRange - 0.5, reference.LoudnessRange + 0.5);
        Assert.InRange(measurement.TruePeakDb, reference.TruePeak - 0.3, reference.TruePeak + 0.3);
    }

    [Fact]
    public async Task Analyze_RealSample_MatchesFfmpegEbur128()
    {
        var ffmpeg = TryFindFfmpeg();
        if (ffmpeg is null)
        {
            Assert.Skip("未找到 ffmpeg.exe，跳过真实样本对照。");
        }

        var sample = TryFindRepositoryFile(Path.Combine(".scratch", "lufs-research", "sample.wav"));
        if (sample is null)
        {
            Assert.Skip("未找到 .scratch\\lufs-research\\sample.wav，跳过真实样本对照。");
        }

        using var workspace = new TempWorkspace();
        var analyzer = CreateAnalyzer(workspace);

        var measurement = await analyzer.AnalyzeAsync(sample);
        var reference = await RunFfmpegEbur128Async(ffmpeg, sample);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, reference.Integrated - 0.3, reference.Integrated + 0.3);
        Assert.NotNull(measurement.LoudnessRange);
        Assert.InRange(measurement.LoudnessRange!.Value, reference.LoudnessRange - 0.5, reference.LoudnessRange + 0.5);
        Assert.InRange(measurement.TruePeakDb, reference.TruePeak - 0.3, reference.TruePeak + 0.3);
    }

    private static LoudnessAnalyzer CreateAnalyzer(TempWorkspace workspace)
    {
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        return new LoudnessAnalyzer(new FileLogger(paths));
    }

    private static string? TryFindFfmpeg()
    {
        var configured = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var bundled = TestTools.ResolveTool("ffmpeg.exe");
        if (bundled is not null)
        {
            return bundled;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
        {
            var candidate = Path.Combine(profile, "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // PATH 里的非法字符目录直接跳过。
            }
        }

        return null;
    }

    private static string? TryFindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EchoGarage.sln")))
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                return File.Exists(candidate) ? candidate : null;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static async Task<(double Integrated, double LoudnessRange, double TruePeak)> RunFfmpegEbur128Async(
        string ffmpeg,
        string wavPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(wavPath);
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("ebur128=peak=true");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("ffmpeg 启动失败。");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        await stdoutTask;
        Assert.True(process.ExitCode == 0, $"ffmpeg ebur128 退出码 {process.ExitCode}：{Truncate(stderr)}");

        return (LastValue(IntegratedRegex, stderr), LastValue(LoudnessRangeRegex, stderr), LastValue(TruePeakRegex, stderr));
    }

    private static double LastValue(Regex regex, string text)
    {
        var matches = regex.Matches(text);
        Assert.True(matches.Count > 0, $"未能从 ffmpeg 输出解析 {regex}：{Truncate(text)}");
        return double.Parse(matches[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Truncate(string text) =>
        text.Length <= 800 ? text : text[..800] + "…";

    /// <summary>同步收集进度回报的 <see cref="IProgress{T}"/> 实现（避免 <c>Progress&lt;T&gt;</c> 的异步回调竞态）。</summary>
    private sealed class CollectingProgress : IProgress<LoudnessAnalysisProgress>
    {
        public List<LoudnessAnalysisProgress> Reports { get; } = [];

        public void Report(LoudnessAnalysisProgress value) => Reports.Add(value);
    }
}

/// <summary>
/// 测试用 PCM WAV 生成器：支持 16/24-bit、单/多声道、可选 WAVE_FORMAT_EXTENSIBLE 声道掩码。
/// 独立于 <see cref="TestAudio"/>，因为分析/增益测试需要指定位深、多声道与逐声道幅度。
/// </summary>
internal static class TestPcmAudio
{
    private static readonly Guid PcmSubFormat = Guid.Parse("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>写一个各声道幅度可不同的恒定正弦 WAV。</summary>
    public static string WriteSineWav(
        string path,
        int channels = 1,
        int sampleRate = 44100,
        int bitDepth = 16,
        double seconds = 2,
        IReadOnlyList<double>? amplitudes = null,
        double frequency = 1000,
        int? channelMask = null)
    {
        var channelAmplitudes = amplitudes ?? Enumerable.Repeat(0.5, channels).ToArray();
        if (channelAmplitudes.Count != channels)
        {
            throw new ArgumentException("声道幅度数量必须与声道数一致。", nameof(amplitudes));
        }

        var frames = (int)Math.Round(sampleRate * seconds);
        return WriteWavCore(
            path,
            channels,
            sampleRate,
            bitDepth,
            frames,
            channelMask,
            (frame, channel) => channelAmplitudes[channel] * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
    }

    /// <summary>写一个幅度随时间变化的立体声/多声道正弦 WAV（用于门限与 LRA 对照）。</summary>
    public static string WriteEnvelopeWav(
        string path,
        int channels = 2,
        int sampleRate = 44100,
        double seconds = 20,
        Func<double, double>? amplitudeAt = null,
        double frequency = 997,
        int bitDepth = 16)
    {
        var envelope = amplitudeAt ?? (_ => 0.5);
        var frames = (int)Math.Round(sampleRate * seconds);
        return WriteWavCore(
            path,
            channels,
            sampleRate,
            bitDepth,
            frames,
            channelMask: null,
            (frame, _) => envelope(frame / (double)sampleRate) * Math.Sin(2 * Math.PI * frequency * frame / sampleRate));
    }

    /// <summary>读取 WAV 的 float32 采样（仅普通 PCM/float WAV；转换输出使用）。</summary>
    public static float[] ReadFloatSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var buffer = new float[8192 * provider.WaveFormat.Channels];
        var samples = new List<float>();
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                samples.Add(buffer[index]);
            }
        }

        return samples.ToArray();
    }

    /// <summary>读取 16-bit WAV 的原始 PCM 样本（用于逐位比较 dither 输出）。</summary>
    public static short[] ReadInt16Samples(string path)
    {
        using var reader = new WaveFileReader(path);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
        var samples = new List<short>();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index + 1 < read; index += 2)
            {
                samples.Add(BitConverter.ToInt16(buffer, index));
            }
        }

        return samples.ToArray();
    }

    /// <summary>计算 float 采样的 RMS（跨声道混合），用于增益幅度断言。</summary>
    public static double RootMeanSquare(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        var sum = 0.0;
        foreach (var sample in samples)
        {
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static string WriteWavCore(
        string path,
        int channels,
        int sampleRate,
        int bitDepth,
        int frames,
        int? channelMask,
        Func<int, int, double> sampleAt)
    {
        var bytesPerSample = bitDepth switch
        {
            16 => 2,
            24 => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(bitDepth), bitDepth, "测试 WAV 只支持 16/24-bit。")
        };
        var blockAlign = channels * bytesPerSample;
        var dataSize = frames * blockAlign;
        var isExtensible = channelMask.HasValue;
        var formatChunkSize = isExtensible ? 40 : 16;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write("RIFF"u8);
        writer.Write(4 + 8 + formatChunkSize + 8 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(formatChunkSize);
        writer.Write(unchecked((short)(isExtensible ? 0xFFFE : 1)));
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)bitDepth);
        if (isExtensible)
        {
            writer.Write((short)22);
            writer.Write((short)bitDepth);
            writer.Write(channelMask!.Value);
            writer.Write(PcmSubFormat.ToByteArray());
        }

        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var value = Math.Clamp(sampleAt(frame, channel), -1.0, 1.0);
                if (bitDepth == 16)
                {
                    writer.Write((short)Math.Round(value * 32767.0, MidpointRounding.AwayFromZero));
                }
                else
                {
                    var sample = (int)Math.Round(value * 8388607.0, MidpointRounding.AwayFromZero);
                    writer.Write((byte)(sample & 0xFF));
                    writer.Write((byte)((sample >> 8) & 0xFF));
                    writer.Write((byte)((sample >> 16) & 0xFF));
                }
            }
        }

        writer.Flush();
        return path;
    }
}
