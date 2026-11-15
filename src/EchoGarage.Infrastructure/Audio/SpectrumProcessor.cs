using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using NAudio.Dsp;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 分段频谱的数值核心（ADR-023 §6.4）：加周期 Hann 窗 → FFT → 按频段聚合幅度 → dBFS → 斜率补偿。
/// 纯计算、同步、可在任意线程调用（内部缓存不是线程安全的，一个实例只应被一个线程使用），便于单测；
/// 环形缓冲与弹道由 <see cref="SpectrumAnalyzer"/> 负责。
/// </summary>
public sealed class SpectrumProcessor
{
    private readonly int[] _bandStartBins;
    private readonly int[] _bandEndBins;
    private readonly double[] _bandGainDb;
    private readonly float[] _window;
    private readonly Complex[] _fft;
    private readonly double[] _bandDb;
    private readonly double _amplitudeScale;

    public SpectrumProcessor(SpectrumOptions options, int sampleRate)
    {
        Options = options;
        SampleRate = Math.Max(8000, sampleRate);
        FftSize = options.FftSize;
        FftExponent = (int)Math.Round(Math.Log2(FftSize));

        // 周期 Hann 窗（分母是 N 而不是 N−1）：相干增益 sum(w)/N 恰好 0.5，幅度标定最简单。
        _window = new float[FftSize];
        var windowSum = 0.0;
        for (var i = 0; i < FftSize; i++)
        {
            var value = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / FftSize));
            _window[i] = (float)value;
            windowSum += value;
        }

        // NAudio 的正向 FFT 已经把每个输出除以 N（ADR-023 §4.4 实测），所以幅度只需再乘
        // 2 / CG（CG = sum(w)/N）：单频正弦 A、频率落在 bin 中心时 |X| = A·CG/2。
        var coherentGain = windowSum / FftSize;
        _amplitudeScale = 2.0 / coherentGain;

        _fft = new Complex[FftSize];
        _bandDb = new double[options.BandCount];
        _bandStartBins = new int[options.BandCount];
        _bandEndBins = new int[options.BandCount];
        _bandGainDb = new double[options.BandCount];

        var maxBin = FftSize / 2;
        var edges = options.BandEdges;
        var previousEnd = 1; // 跳过直流分量，避免偏置把最低频段顶起来
        for (var band = 0; band < options.BandCount; band++)
        {
            var lower = edges[band];
            var upper = edges[band + 1];
            var start = (int)Math.Round(lower * FftSize / SampleRate, MidpointRounding.AwayFromZero);
            var end = (int)Math.Round(upper * FftSize / SampleRate, MidpointRounding.AwayFromZero);
            start = Math.Clamp(Math.Max(start, previousEnd), 1, maxBin);
            end = Math.Clamp(Math.Max(end, start + 1), start + 1, maxBin + 1);
            _bandStartBins[band] = start;
            _bandEndBins[band] = end;
            previousEnd = end;
            _bandGainDb[band] = SpectrumMath.SlopeGainDb(
                SpectrumMath.BandCenter(lower, upper),
                options.SlopeDbPerOctave);
        }
    }

    /// <summary>参数快照（引用不变即表示参数未变）。</summary>
    public SpectrumOptions Options { get; }

    /// <summary>分析使用的采样率。</summary>
    public int SampleRate { get; }

    /// <summary>FFT 点数。</summary>
    public int FftSize { get; }

    /// <summary>FFT 点数对应的 2 的幂。</summary>
    public int FftExponent { get; }

    /// <summary>频段数。</summary>
    public int BandCount => _bandDb.Length;

    /// <summary>上一帧是否整段低于静音阈值（-100 dBFS）。</summary>
    public bool LastFrameSilent { get; private set; }

    /// <summary>
    /// 计算各频段的 dBFS（已含斜率补偿）。样本不足 FFT 长度时左补零；多于 FFT 长度时只用最新的一段。
    /// 返回内部数组，调用方只读。
    /// </summary>
    public double[] ComputeBandDb(ReadOnlySpan<float> mono)
    {
        var count = Math.Min(FftSize, mono.Length);
        var offset = mono.Length - count;
        var peak = 0f;
        for (var i = 0; i < FftSize; i++)
        {
            var sample = i >= count ? 0f : mono[offset + i];
            var absolute = Math.Abs(sample);
            if (absolute > peak)
            {
                peak = absolute;
            }

            _fft[i].X = sample * _window[i];
            _fft[i].Y = 0f;
        }

        LastFrameSilent = peak < 1e-4f;

        FastFourierTransform.FFT(true, FftExponent, _fft);

        for (var band = 0; band < _bandDb.Length; band++)
        {
            var start = _bandStartBins[band];
            var end = _bandEndBins[band];
            double peakAmplitude = 0.0;
            double powerSum = 0.0;
            for (var bin = start; bin < end; bin++)
            {
                var real = _fft[bin].X;
                var imaginary = _fft[bin].Y;
                var amplitude = Math.Sqrt(real * real + imaginary * imaginary) * _amplitudeScale;
                powerSum += amplitude * amplitude;
                if (amplitude > peakAmplitude)
                {
                    peakAmplitude = amplitude;
                }
            }

            var bandAmplitude = Options.Summation == SpectrumSummation.Peak
                ? peakAmplitude
                : Math.Sqrt(powerSum / (end - start));
            _bandDb[band] = SpectrumMath.AmplitudeToDb(bandAmplitude) + _bandGainDb[band];
        }

        return _bandDb;
    }
}
