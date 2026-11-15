using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;

namespace EchoGarage.Tests;

/// <summary>ADR-023 §6.4 的数值核心：加窗 / FFT / 频段聚合 / dBFS 标定。</summary>
public sealed class SpectrumProcessorTests
{
    private const int SampleRate = 48000;
    private const int FftSize = 4096;

    private static SpectrumOptions CreateOptions(
        SpectrumSummation summation = SpectrumSummation.Peak,
        int bandCount = 40,
        double slopeDbPerOctave = 0,
        int fftSize = FftSize,
        double minFrequency = 40,
        double maxFrequency = 18000) =>
        SpectrumOptions.From(new SpectrumSettings
        {
            BandCount = bandCount,
            FftSize = fftSize,
            MinFrequency = minFrequency,
            MaxFrequency = maxFrequency,
            Summation = summation,
            SlopeDbPerOctave = slopeDbPerOctave,
            FloorDb = -70,
        });

    private static float[] CreateTone(double frequency, double amplitude, int fftSize = FftSize)
    {
        var samples = new float[fftSize];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * i / SampleRate));
        }

        return samples;
    }

    private static int FindBand(SpectrumOptions options, double frequency)
    {
        for (var band = 0; band < options.BandCount; band++)
        {
            if (frequency >= options.BandEdges[band] && frequency < options.BandEdges[band + 1])
            {
                return band;
            }
        }

        return -1;
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    public void ComputeBandDb_CalibratesBinCenteredToneToDbfs(int fftSize)
    {
        var options = CreateOptions(fftSize: fftSize);
        var processor = new SpectrumProcessor(options, SampleRate);

        // 取最靠近 1 kHz 的 bin 中心频率，避免 Hann 泄漏影响幅度标定。
        var nearestBin = Math.Round(1000.0 * fftSize / SampleRate);
        var frequency = nearestBin * SampleRate / fftSize;
        var samples = CreateTone(frequency, 0.5, fftSize);
        var db = processor.ComputeBandDb(samples);

        var loudest = Array.IndexOf(db, db.Max());
        Assert.Equal(FindBand(options, frequency), loudest);
        // 幅度 0.5 → −6.02 dBFS；峰值聚合取最强 bin，标定误差应远小于 1 dB。
        Assert.InRange(db[loudest], -6.9, -5.2);
    }

    [Fact]
    public void ComputeBandDb_AppliesSlopeCompensation()
    {
        var options = CreateOptions(slopeDbPerOctave: 3);
        var flat = new SpectrumProcessor(CreateOptions(slopeDbPerOctave: 0), SampleRate);
        var sloped = new SpectrumProcessor(options, SampleRate);

        var flatDb = flat.ComputeBandDb(CreateTone(1000, 0.5)).ToArray();
        var slopedDb = sloped.ComputeBandDb(CreateTone(1000, 0.5)).ToArray();
        var flatHighDb = flat.ComputeBandDb(CreateTone(8000, 0.5)).ToArray();
        var slopedHighDb = sloped.ComputeBandDb(CreateTone(8000, 0.5)).ToArray();

        // 斜率补偿按“频段中心频率”算，所以逐段比对增量，而不是假定 1 kHz 段增量为 0。
        var midIndex = Array.IndexOf(flatDb, flatDb.Max());
        var highIndex = Array.IndexOf(flatHighDb, flatHighDb.Max());
        var midGain = BandCenterGain(options, midIndex);
        var highGain = BandCenterGain(options, highIndex);

        Assert.Equal(flatDb[midIndex] + midGain, slopedDb[midIndex], 1);
        Assert.Equal(flatHighDb[highIndex] + highGain, slopedHighDb[highIndex], 1);
        Assert.True(highGain > midGain + 6.0, "8 kHz 的斜率增益应明显高于 1 kHz 附近");
    }

    private static double BandCenterGain(SpectrumOptions options, int band)
    {
        var center = SpectrumMath.BandCenter(options.BandEdges[band], options.BandEdges[band + 1]);
        return SpectrumMath.SlopeGainDb(center, options.SlopeDbPerOctave);
    }

    [Fact]
    public void ComputeBandDb_RmsSummationKeepsBroadbandNoiseFlat()
    {
        var options = CreateOptions(SpectrumSummation.Rms);
        var processor = new SpectrumProcessor(options, SampleRate);
        var random = new Random(20240517);
        var averages = new double[options.BandCount];

        const int frames = 16;
        for (var frame = 0; frame < frames; frame++)
        {
            var samples = new float[FftSize];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)((random.NextDouble() * 2.0 - 1.0) * 0.1);
            }

            var db = processor.ComputeBandDb(samples);
            for (var band = 0; band < averages.Length; band++)
            {
                averages[band] += db[band];
            }
        }

        for (var band = 0; band < averages.Length; band++)
        {
            averages[band] /= frames;
        }

        // 平均功率聚合是“每 bin 的功率密度”，白噪下各段应基本持平；
        // 若换成取最强 bin，高频段会因为 bin 多而整体抬高（见下一个用例）。
        var spread = averages.Max() - averages.Min();
        Assert.True(spread < 7.0, $"白噪各段应大致平坦，实际跨度 {spread:F1} dB");
    }

    [Fact]
    public void ComputeBandDb_PeakSummationTiltsBroadbandNoiseUpward()
    {
        var options = CreateOptions(SpectrumSummation.Peak);
        var processor = new SpectrumProcessor(options, SampleRate);
        var random = new Random(20240517);
        var averages = new double[options.BandCount];

        const int frames = 16;
        for (var frame = 0; frame < frames; frame++)
        {
            var samples = new float[FftSize];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)((random.NextDouble() * 2.0 - 1.0) * 0.1);
            }

            var db = processor.ComputeBandDb(samples);
            for (var band = 0; band < averages.Length; band++)
            {
                averages[band] += db[band];
            }
        }

        for (var band = 0; band < averages.Length; band++)
        {
            averages[band] /= frames;
        }

        var spread = averages.Max() - averages.Min();
        Assert.True(spread > 8.0, $"取最大 bin 时高频段应被抬高，实际跨度 {spread:F1} dB");
    }

    [Fact]
    public void ComputeBandDb_SilenceReportsFloorAndSetsSilentFlag()
    {
        var options = CreateOptions(slopeDbPerOctave: 0);
        var processor = new SpectrumProcessor(options, SampleRate);

        var db = processor.ComputeBandDb(new float[FftSize]);

        Assert.True(processor.LastFrameSilent);
        Assert.All(db, value => Assert.True(value <= SpectrumMath.MinimumAmplitudeDb + 0.001));
    }

    [Fact]
    public void ComputeBandDb_DcOffsetDoesNotLiftLowestBand()
    {
        var options = CreateOptions(SpectrumSummation.Rms);
        var processor = new SpectrumProcessor(options, SampleRate);
        var samples = new float[FftSize];
        Array.Fill(samples, 1.0f);

        var db = processor.ComputeBandDb(samples);

        // 直流被挡在频段之外（跳过 bin 0），最低段不应被偏置顶起来。
        Assert.True(db[0] < -40.0, $"最低段被直流抬高：{db[0]:F1} dB");
    }

    [Fact]
    public void ComputeBandDb_UsesNewestSamplesWhenInputIsLonger()
    {
        var options = CreateOptions(bandCount: 8, minFrequency: 100, maxFrequency: 5000);
        var processor = new SpectrumProcessor(options, SampleRate);
        var silenceThenTone = new float[FftSize * 2];
        var tone = CreateTone(1000, 0.5);
        Array.Copy(tone, 0, silenceThenTone, FftSize, FftSize);

        var db = processor.ComputeBandDb(silenceThenTone);

        Assert.True(db.Max() > -20, $"较长的输入应使用最新一段（含 1 kHz 单音），实际最大 {db.Max():F1} dB");
    }
}
