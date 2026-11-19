using System.Numerics;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-028 §4.1：<see cref="LoudnessMeter"/> 的 K 加权系数、积分响度两遍门限、LRA、
/// 真峰值（ITU 4×48-tap）与短文件 / 静音 / 分块等边界行为。
/// 全部用合成 float 样本直接喂表，不依赖文件、解码器或 NAudio。
/// </summary>
public sealed class LoudnessMeterTests
{
    private const int StandardSampleRate = 48000;

    /// <summary>0.5 线性幅度对应的 dBFS（真峰值测试用的半幅正弦）。</summary>
    private const double HalfAmplitudeDb = -6.020599913279624;

    [Fact]
    public void DesignKWeighting_At48k_MatchesOfficialTable()
    {
        var coefficients = LoudnessMeter.DesignKWeighting(StandardSampleRate);

        // BS.1770-5 Table 1 / Table 2 的官方系数（KB-019 §3）。
        Assert.Equal(1.53512485958697, coefficients.PreFilter.B0, 9);
        Assert.Equal(-2.69169618940638, coefficients.PreFilter.B1, 9);
        Assert.Equal(1.19839281085285, coefficients.PreFilter.B2, 9);
        Assert.Equal(-1.69065929318241, coefficients.PreFilter.A1, 9);
        Assert.Equal(0.73248077421585, coefficients.PreFilter.A2, 9);

        Assert.Equal(1.0, coefficients.RlbFilter.B0, 9);
        Assert.Equal(-2.0, coefficients.RlbFilter.B1, 9);
        Assert.Equal(1.0, coefficients.RlbFilter.B2, 9);
        Assert.Equal(-1.99004745483398, coefficients.RlbFilter.A1, 9);
        Assert.Equal(0.99007225036621, coefficients.RlbFilter.A2, 9);
    }

    [Fact]
    public void DesignKWeighting_At44k_KeepsDcGainAndKCurve()
    {
        var coefficients = LoudnessMeter.DesignKWeighting(44100);

        Assert.True(double.IsFinite(coefficients.PreFilter.B0));
        Assert.True(double.IsFinite(coefficients.PreFilter.B2));
        Assert.True(double.IsFinite(coefficients.RlbFilter.A2));

        // pre-filter 通带（DC）增益 0 dB，RLB 是 DC 阻塞高通（DC 增益 0）。
        Assert.Equal(1.0, Magnitude(coefficients.PreFilter, 0.0, 44100), 6);
        Assert.Equal(0.0, Magnitude(coefficients.RlbFilter, 0.0, 44100), 6);

        // K 加权在 1 kHz 附近的总增益约 +0.691 dB（BS.1770 的标定基准）。
        var combinedDb = MagnitudeDb(coefficients.PreFilter, 1000.0, 44100)
            + MagnitudeDb(coefficients.RlbFilter, 1000.0, 44100);
        Assert.InRange(combinedDb, 0.66, 0.72);
    }

    [Fact]
    public void DesignKWeighting_RejectsTooLowSampleRate()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoudnessMeter.DesignKWeighting(4000));
        Assert.Throws<ArgumentOutOfRangeException>(() => LoudnessMeter.DesignKWeighting(0));
    }

    [Fact]
    public void Meter_RejectsInvalidConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new LoudnessMeter(4000, [1.0]));
        Assert.Throws<ArgumentException>(() => new LoudnessMeter(StandardSampleRate, []));
        Assert.Throws<ArgumentException>(() => new LoudnessMeter(StandardSampleRate, [1.0, -0.1]));
        Assert.Throws<ArgumentException>(() => new LoudnessMeter(StandardSampleRate, [1.0, double.NaN]));
    }

    [Fact]
    public void Process_RejectsInvalidArguments()
    {
        var meter = new LoudnessMeter(StandardSampleRate, [1.0, 1.0], enableTruePeak: false);
        var samples = new float[200];

        Assert.Throws<ArgumentOutOfRangeException>(() => meter.Process(samples, -1));
        Assert.Throws<ArgumentException>(() => meter.Process(samples, 101));
    }

    [Fact]
    public void IntegratedLufs_Stereo1kHz_At48k_IsMinus23()
    {
        // 每声道幅度 -23 dBFS 的 1 kHz 正弦（立体声相加 +3.01 dB、K 加权 +0.691、偏移 -0.691）→ I = -23 LUFS。
        var samples = CreateSine(
            StandardSampleRate,
            [-23.0, -23.0],
            frequency: 1000.0,
            seconds: 20.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -23.1, -22.9);
        Assert.Null(measurement.Note);
        Assert.Equal(StandardSampleRate, measurement.SampleRate);
        Assert.Equal(2, measurement.Channels);
    }

    [Fact]
    public void IntegratedLufs_Stereo1kHz_At44k_IsMinus23()
    {
        var samples = CreateSine(44100, [-23.0, -23.0], frequency: 1000.0, seconds: 20.0);

        var measurement = Measure(44100, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void IntegratedLufs_LeftChannelOnly_IsMinus26()
    {
        // 只有左声道：能量少一半（-3.01 dB）→ -26.01 LUFS。
        var samples = CreateSine(
            StandardSampleRate,
            [-23.0, double.NegativeInfinity],
            frequency: 1000.0,
            seconds: 5.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -26.1, -25.9);
    }

    [Fact]
    public void IntegratedLufs_LowFrequency_AppliesKWeighting()
    {
        // 100 Hz：K 加权约 -1.13 dB（相对 1 kHz）→ I ≈ -24.8 LUFS。
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 100.0, seconds: 5.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -25.0, -24.6);
    }

    [Fact]
    public void IntegratedLufs_HighFrequency_AppliesKWeighting()
    {
        // 4 kHz：K 加权高频平台约 +3.97 dB → I ≈ -19.7 LUFS。
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 4000.0, seconds: 5.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -19.9, -19.5);
    }

    [Fact]
    public void IntegratedLufs_AppliesRelativeGate_Tech3341Case3()
    {
        // Tech 3341 #3：-36 LUFS 10 s + -23 LUFS 60 s + -36 LUFS 10 s。
        // 相对门限 -10 LU 后 -36 段被门限排除，结果应为 -23 LUFS。
        var samples = CreateLevelSteps(
            StandardSampleRate,
            channels: 2,
            frequency: 1000.0,
            (-36.0, 10.0),
            (-23.0, 60.0),
            (-36.0, 10.0));

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void IntegratedLufs_IgnoresBelowAbsoluteGate_Tech3341Case4()
    {
        // Tech 3341 #4：-72 LUFS 段低于 -70 LUFS 绝对门限，必须完全忽略。
        var samples = CreateLevelSteps(
            StandardSampleRate,
            channels: 2,
            frequency: 1000.0,
            (-72.0, 10.0),
            (-36.0, 10.0),
            (-23.0, 60.0),
            (-36.0, 10.0),
            (-72.0, 10.0));

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void IntegratedLufs_Surround_UsesChannelWeightsAndExcludesLfe()
    {
        // 5.1：L/R -28、C -24、Ls/Rs -30（权重 1.41）、LFE 满幅 0 dBFS（权重 0）。
        // 若 LFE 被计入，I 会升到约 -3 LUFS；正确结果是 -23.0 ± 0.2。
        var samples = CreateSine(
            StandardSampleRate,
            [-28.0, -28.0, -24.0, 0.0, -30.0, -30.0],
            frequency: 1000.0,
            seconds: 2.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 6);

        Assert.NotNull(measurement.IntegratedLufs);
        Assert.InRange(measurement.IntegratedLufs!.Value, -23.2, -22.8);
    }

    [Fact]
    public void LoudnessRange_20sMinus20_And_20sMinus30_IsTen()
    {
        // EBU Tech 3342 参考用例：20 s @ -20 LUFS + 20 s @ -30 LUFS → LRA = 10 LU。
        var samples = CreateLevelSteps(
            StandardSampleRate,
            channels: 2,
            frequency: 1000.0,
            (-20.0, 20.0),
            (-30.0, 20.0));

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.LoudnessRange);
        Assert.InRange(measurement.LoudnessRange!.Value, 9.5, 10.5);
    }

    [Fact]
    public void LoudnessRange_ShortFile_IsNull()
    {
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 1000.0, seconds: 2.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.Null(measurement.LoudnessRange);
        Assert.Null(measurement.MaxShortTermLufs);
        Assert.NotNull(measurement.MaxMomentaryLufs);
        Assert.InRange(measurement.MaxMomentaryLufs!.Value, -23.1, -22.9);
    }

    [Fact]
    public void LoudnessRange_Silence_IsNull()
    {
        var samples = CreateLevelSteps(
            StandardSampleRate,
            channels: 2,
            frequency: 1000.0,
            (double.NegativeInfinity, 5.0));

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.Null(measurement.IntegratedLufs);
        Assert.Null(measurement.LoudnessRange);

        // 5 s 静音已能算出 3 s 短时窗（最大值是 -∞），只是没有 ≥ -70 LUFS 的值可用于 LRA。
        Assert.Equal(double.NegativeInfinity, measurement.MaxShortTermLufs!.Value);
        Assert.Equal(double.NegativeInfinity, measurement.MaxMomentaryLufs!.Value);
        Assert.NotNull(measurement.Note);
    }

    [Fact]
    public void ShortRun_MomentaryExists_ButShortTermDoesNot()
    {
        // 1 s 信号：400 ms 块足够（Momentary 可测），但 3 s 短时窗不足（S/LRA 不可测）。
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 1000.0, seconds: 1.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.MaxMomentaryLufs);
        Assert.InRange(measurement.MaxMomentaryLufs!.Value, -23.1, -22.9);
        Assert.Null(measurement.MaxShortTermLufs);
        Assert.Null(measurement.LoudnessRange);
    }

    [Fact]
    public void SteadyFiveSecondSignal_HasZeroLra()
    {
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 1000.0, seconds: 5.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.NotNull(measurement.MaxShortTermLufs);
        Assert.InRange(measurement.MaxShortTermLufs!.Value, -23.1, -22.9);
        Assert.NotNull(measurement.LoudnessRange);
        Assert.True(Math.Abs(measurement.LoudnessRange!.Value) < 1e-6,
            $"稳态信号的 LRA 应为 0，实际 {measurement.LoudnessRange:F6} LU。");
    }

    [Fact]
    public void TruePeak_FsQuarterSineAt45Degrees_IsMinus6()
    {
        // 0.5 幅度 fs/4 正弦、45° 相位：采样点恰在 ±A/√2，采样峰值 -9.03 dBFS；
        // 真峰值应接近理论峰 0.5（-6.02 dBTP，KB-019 原型实测 -5.98）。
        var samples = CreateSine(
            StandardSampleRate,
            [HalfAmplitudeDb],
            frequency: StandardSampleRate / 4.0,
            seconds: 1.0,
            phase: Math.PI / 4.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 1, enableTruePeak: true);

        Assert.InRange(measurement.SamplePeakDb, -9.08, -8.98);
        Assert.InRange(measurement.TruePeakDb, -6.4, -5.8);
    }

    [Fact]
    public void TruePeak_1kHzHalfAmplitude_IsMinus6()
    {
        var samples = CreateSine(
            StandardSampleRate,
            [HalfAmplitudeDb],
            frequency: 1000.0,
            seconds: 1.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 1, enableTruePeak: true);

        Assert.InRange(measurement.TruePeakDb, -6.15, -5.9);
    }

    [Fact]
    public void TruePeak_NegativePhase_UsesAbsoluteValue()
    {
        var positive = CreateSine(
            StandardSampleRate,
            [HalfAmplitudeDb],
            frequency: StandardSampleRate / 4.0,
            seconds: 1.0,
            phase: Math.PI / 4.0);
        var negative = CreateSine(
            StandardSampleRate,
            [HalfAmplitudeDb],
            frequency: StandardSampleRate / 4.0,
            seconds: 1.0,
            phase: Math.PI / 4.0 + Math.PI);

        var positiveResult = Measure(StandardSampleRate, positive, channels: 1, enableTruePeak: true);
        var negativeResult = Measure(StandardSampleRate, negative, channels: 1, enableTruePeak: true);

        Assert.True(Math.Abs(positiveResult.TruePeakDb - negativeResult.TruePeakDb) < 1e-9,
            $"反相后真峰值应相同：{positiveResult.TruePeakDb:F6} vs {negativeResult.TruePeakDb:F6}。");
    }

    [Fact]
    public void TruePeak_IncludesLfeChannel()
    {
        // 只有 LFE（索引 3）有信号：LFE 权重 0 → 积分响度不可测，但真峰值必须包含 LFE。
        var samples = CreateSine(
            StandardSampleRate,
            [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity, HalfAmplitudeDb,
                double.NegativeInfinity, double.NegativeInfinity],
            frequency: StandardSampleRate / 4.0,
            seconds: 1.0,
            phase: Math.PI / 4.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 6, enableTruePeak: true);

        Assert.Null(measurement.IntegratedLufs);
        Assert.InRange(measurement.TruePeakDb, -6.4, -5.8);
    }

    [Fact]
    public void TruePeak_Disabled_FallsBackToSamplePeak()
    {
        var samples = CreateSine(
            StandardSampleRate,
            [HalfAmplitudeDb],
            frequency: StandardSampleRate / 4.0,
            seconds: 1.0,
            phase: Math.PI / 4.0);

        var measurement = Measure(StandardSampleRate, samples, channels: 1, enableTruePeak: false);

        Assert.Equal(measurement.SamplePeakDb, measurement.TruePeakDb, 12);
    }

    [Fact]
    public void ShortFile_Under400ms_IntegratedIsNull()
    {
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 1000.0, seconds: 0.3);

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.Null(measurement.IntegratedLufs);
        Assert.Null(measurement.MaxMomentaryLufs);
        Assert.NotNull(measurement.Note);
        Assert.True(double.IsFinite(measurement.SamplePeakDb));
    }

    [Fact]
    public void Silence_ReturnsNullIntegratedWithNote()
    {
        var samples = CreateLevelSteps(
            StandardSampleRate,
            channels: 2,
            frequency: 1000.0,
            (double.NegativeInfinity, 2.0));

        var measurement = Measure(StandardSampleRate, samples, channels: 2);

        Assert.Null(measurement.IntegratedLufs);
        Assert.Equal(double.NegativeInfinity, measurement.SamplePeakDb);
        Assert.Equal(double.NegativeInfinity, measurement.TruePeakDb);
        Assert.Equal(double.NegativeInfinity, measurement.MaxMomentaryLufs!.Value);
        Assert.NotNull(measurement.Note);
    }

    [Fact]
    public void Complete_WithoutSamples_ReturnsNullInsteadOfThrowing()
    {
        var meter = new LoudnessMeter(StandardSampleRate, [1.0, 1.0], enableTruePeak: false);

        var measurement = meter.Complete(TimeSpan.Zero);

        Assert.Null(measurement.IntegratedLufs);
        Assert.Null(measurement.LoudnessRange);
        Assert.Null(measurement.MaxMomentaryLufs);
        Assert.Null(measurement.MaxShortTermLufs);
        Assert.Equal(0, meter.ProcessedFrames);
        Assert.NotNull(measurement.Note);
    }

    [Fact]
    public void ChunkedProcessing_MatchesSinglePass()
    {
        var samples = CreateSine(StandardSampleRate, [-23.0, -23.0], frequency: 1000.0, seconds: 5.0);

        var singlePass = Measure(StandardSampleRate, samples, channels: 2, enableTruePeak: true);
        var chunked = MeasureChunked(
            StandardSampleRate,
            samples,
            channels: 2,
            enableTruePeak: true,
            chunkFrames: [100, 7, 1001, 13]);

        Assert.True(Math.Abs(singlePass.IntegratedLufs!.Value - chunked.IntegratedLufs!.Value) < 1e-9);
        Assert.True(Math.Abs(singlePass.LoudnessRange!.Value - chunked.LoudnessRange!.Value) < 1e-9);
        Assert.True(Math.Abs(singlePass.TruePeakDb - chunked.TruePeakDb) < 1e-9);
        Assert.True(Math.Abs(singlePass.SamplePeakDb - chunked.SamplePeakDb) < 1e-9);
        Assert.True(Math.Abs(singlePass.MaxMomentaryLufs!.Value - chunked.MaxMomentaryLufs!.Value) < 1e-9);
    }

    /// <summary>构造每声道给定幅度（dBFS）的正弦；-∞ 表示该声道静音。</summary>
    private static float[] CreateSine(
        int sampleRate,
        IReadOnlyList<double> channelLevelDb,
        double frequency,
        double seconds,
        double phase = 0.0)
    {
        var channels = channelLevelDb.Count;
        var frames = (int)Math.Round(seconds * sampleRate);
        var samples = new float[frames * channels];
        var step = 2.0 * Math.PI * frequency / sampleRate;
        var index = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var levelDb = channelLevelDb[channel];
                if (double.IsNegativeInfinity(levelDb))
                {
                    samples[index++] = 0.0f;
                    continue;
                }

                var linear = Math.Pow(10.0, levelDb / 20.0);
                samples[index++] = (float)(linear * Math.Sin(phase + step * frame));
            }
        }

        return samples;
    }

    /// <summary>构造分段恒幅信号（相位连续，包络瞬变只在段边界且能量可忽略）；-∞ 表示静音段。</summary>
    private static float[] CreateLevelSteps(
        int sampleRate,
        int channels,
        double frequency,
        params (double LevelDb, double Seconds)[] segments)
    {
        var totalFrames = 0;
        foreach (var (_, seconds) in segments)
        {
            totalFrames += (int)Math.Round(seconds * sampleRate);
        }

        var samples = new float[totalFrames * channels];
        var step = 2.0 * Math.PI * frequency / sampleRate;
        var frameIndex = 0;
        foreach (var (levelDb, seconds) in segments)
        {
            var frames = (int)Math.Round(seconds * sampleRate);
            var linear = double.IsNegativeInfinity(levelDb) ? 0.0 : Math.Pow(10.0, levelDb / 20.0);
            for (var frame = 0; frame < frames; frame++)
            {
                var value = (float)(linear * Math.Sin(step * frameIndex));
                for (var channel = 0; channel < channels; channel++)
                {
                    samples[frameIndex * channels + channel] = value;
                }

                frameIndex++;
            }
        }

        return samples;
    }

    /// <summary>一次喂入全部样本并汇总。</summary>
    private static LoudnessMeasurement Measure(
        int sampleRate,
        float[] interleaved,
        int channels,
        bool enableTruePeak = false)
    {
        var meter = new LoudnessMeter(sampleRate, CreateChannelWeights(channels), enableTruePeak);
        meter.Process(interleaved, interleaved.Length / channels);
        var duration = TimeSpan.FromSeconds(interleaved.Length / (double)channels / sampleRate);
        return meter.Complete(duration);
    }

    /// <summary>按给定帧数模式分块喂入（验证流式状态与分块边界无关）。</summary>
    private static LoudnessMeasurement MeasureChunked(
        int sampleRate,
        float[] interleaved,
        int channels,
        bool enableTruePeak,
        int[] chunkFrames)
    {
        var meter = new LoudnessMeter(sampleRate, CreateChannelWeights(channels), enableTruePeak);
        var totalFrames = interleaved.Length / channels;
        var offset = 0;
        var chunk = 0;
        while (offset < totalFrames)
        {
            var frames = Math.Min(chunkFrames[chunk % chunkFrames.Length], totalFrames - offset);
            meter.Process(interleaved.AsSpan(offset * channels, frames * channels), frames);
            offset += frames;
            chunk++;
        }

        var duration = TimeSpan.FromSeconds(totalFrames / (double)sampleRate);
        return meter.Complete(duration);
    }

    /// <summary>按 ADR-028 §3.3 的声道数回退规则生成权重（6 声道 = 5.1，LFE 权重 0）。</summary>
    private static double[] CreateChannelWeights(int channels) => channels switch
    {
        1 => [1.0],
        2 => [1.0, 1.0],
        6 => [1.0, 1.0, 1.0, 0.0, 1.41, 1.41],
        _ => throw new ArgumentOutOfRangeException(nameof(channels)),
    };

    /// <summary>双二阶滤波器在给定频率的幅度响应（线性）。</summary>
    private static double Magnitude(
        LoudnessMeter.BiquadCoefficients coefficients,
        double frequency,
        int sampleRate)
    {
        var omega = 2.0 * Math.PI * frequency / sampleRate;
        var z1 = Complex.Exp(new Complex(0.0, -omega));
        var z2 = z1 * z1;
        var numerator = coefficients.B0 + coefficients.B1 * z1 + coefficients.B2 * z2;
        var denominator = 1.0 + coefficients.A1 * z1 + coefficients.A2 * z2;
        return (numerator / denominator).Magnitude;
    }

    /// <summary>双二阶滤波器在给定频率的幅度响应（dB）。</summary>
    private static double MagnitudeDb(
        LoudnessMeter.BiquadCoefficients coefficients,
        double frequency,
        int sampleRate) =>
        20.0 * Math.Log10(Magnitude(coefficients, frequency, sampleRate));
}
