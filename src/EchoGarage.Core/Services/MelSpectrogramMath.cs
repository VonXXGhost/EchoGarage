namespace EchoGarage.Core.Services;

/// <summary>
/// 严格对齐 Beat This!（ISMIR 2024）官方训练前端的纯 C# 128-bin Slaney Log-Mel 频谱图提取器。
/// <para>
/// 参数经与社区导出的 <c>logmel.onnx</c>（torchaudio 2.3+）实测核验（平均绝对误差 ~1e-6，最大误差 &lt;1e-4）：
/// 22,050 Hz 单声道、N=1024 周期性 Hann 窗、center reflect padding 512、hop=441（50 fps）、
/// STFT 幅度谱（power=1）除以 <c>sqrt(1024)=32</c>（<c>normalized="frame_length"</c>）、
/// 128 组 Slaney 频标三角滤波器（30–11,000 Hz，不做面积归一）、对数压扩 <c>ln(1 + 1000 * S)</c>。
/// </para>
/// </summary>
public static class MelSpectrogramMath
{
    public const int SampleRate = 22050;
    public const int FftSize = 1024;
    public const int HalfFft = FftSize / 2;       // 512
    public const int NumFreqBins = HalfFft + 1;   // 513
    public const int HopLength = 441;             // 22050 / 441 = 50 fps
    public const int NumMels = 128;
    public const double FMinHz = 30.0;
    public const double FMaxHz = 11000.0;
    public const float LogScale = 1000.0f;

    private const double SlaneyFSp = 200.0 / 3.0;
    private const double SlaneyMinLogHz = 1000.0;
    private const double SlaneyMinLogMel = SlaneyMinLogHz / SlaneyFSp; // 15.0
    private static readonly double SlaneyLogStep = Math.Log(6.4) / 27.0;
    private static readonly double StftNormDivisor = Math.Sqrt(FftSize); // 32.0

    private static readonly double[] PeriodicHannWindow = BuildPeriodicHannWindow();
    private static readonly int[] BitReversal = BuildBitReversal(FftSize);
    private static readonly double[] TwiddleCos = BuildTwiddleCos(FftSize);
    private static readonly double[] TwiddleSin = BuildTwiddleSin(FftSize);
    private static readonly SparseMelBand[] MelBands = BuildSlaneyMelFilterbank();

    private readonly record struct SparseMelBand(int StartBin, double[] Weights);

    /// <summary>给定 22,050 Hz 单声道样本数，返回 center-padded STFT 的总帧数（50 fps）。</summary>
    public static int ComputeFrameCount(int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return 0;
        }

        var paddedLength = sampleCount + FftSize; // pad 512 on each side
        return 1 + (paddedLength - FftSize) / HopLength;
    }

    /// <summary>
    /// 提取整段 22,050 Hz 单声道音频的 Log-Mel 频谱图，返回行优先展平数组 <c>[frame * 128 + mel]</c>。
    /// </summary>
    public static (float[] Data, int FrameCount) Compute(ReadOnlySpan<float> mono22kSamples)
    {
        var n = mono22kSamples.Length;
        if (n == 0)
        {
            return ([], 0);
        }

        var frameCount = ComputeFrameCount(n);
        var output = new float[frameCount * NumMels];
        var re = new double[FftSize];
        var im = new double[FftSize];
        var mag = new double[NumFreqBins];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var centerStart = frame * HopLength - HalfFft;
            for (var i = 0; i < FftSize; i++)
            {
                var sampleIndex = ReflectIndex(centerStart + i, n);
                re[i] = mono22kSamples[sampleIndex] * PeriodicHannWindow[i];
                im[i] = 0.0;
            }

            FftInPlace(re, im);

            for (var k = 0; k < NumFreqBins; k++)
            {
                mag[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / StftNormDivisor;
            }

            var rowOffset = frame * NumMels;
            for (var m = 0; m < NumMels; m++)
            {
                var band = MelBands[m];
                var weights = band.Weights;
                var start = band.StartBin;
                double sum = 0.0;
                for (var j = 0; j < weights.Length; j++)
                {
                    sum += mag[start + j] * weights[j];
                }

                output[rowOffset + m] = MathF.Log(1.0f + LogScale * (float)sum);
            }
        }

        return (output, frameCount);
    }

    /// <summary>与 PyTorch / NumPy <c>mode='reflect'</c> 一致的边界反射索引（不含端点重复）。</summary>
    internal static int ReflectIndex(int index, int length)
    {
        if (length <= 1)
        {
            return 0;
        }

        var period = 2 * (length - 1);
        var m = index % period;
        if (m < 0)
        {
            m += period;
        }

        return m < length ? m : period - m;
    }

    internal static double HzToMelSlaney(double hz) =>
        hz >= SlaneyMinLogHz
            ? SlaneyMinLogMel + Math.Log(hz / SlaneyMinLogHz) / SlaneyLogStep
            : hz / SlaneyFSp;

    internal static double MelToHzSlaney(double mel) =>
        mel >= SlaneyMinLogMel
            ? SlaneyMinLogHz * Math.Exp(SlaneyLogStep * (mel - SlaneyMinLogMel))
            : SlaneyFSp * mel;

    private static double[] BuildPeriodicHannWindow()
    {
        var win = new double[FftSize];
        for (var i = 0; i < FftSize; i++)
        {
            win[i] = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / FftSize);
        }

        return win;
    }

    private static SparseMelBand[] BuildSlaneyMelFilterbank()
    {
        var allFreqs = new double[NumFreqBins];
        var nyquist = SampleRate / 2.0;
        for (var k = 0; k < NumFreqBins; k++)
        {
            allFreqs[k] = nyquist * k / HalfFft;
        }

        var minMel = HzToMelSlaney(FMinHz);
        var maxMel = HzToMelSlaney(FMaxHz);
        var fPts = new double[NumMels + 2];
        for (var m = 0; m < fPts.Length; m++)
        {
            var mel = minMel + (maxMel - minMel) * m / (NumMels + 1);
            fPts[m] = MelToHzSlaney(mel);
        }

        var bands = new SparseMelBand[NumMels];
        for (var m = 0; m < NumMels; m++)
        {
            var fLeft = fPts[m];
            var fCenter = fPts[m + 1];
            var fRight = fPts[m + 2];
            var leftSpan = fCenter - fLeft;
            var rightSpan = fRight - fCenter;

            var start = -1;
            var end = -1;
            var dense = new double[NumFreqBins];
            for (var k = 0; k < NumFreqBins; k++)
            {
                var f = allFreqs[k];
                var down = leftSpan > 0 ? (f - fLeft) / leftSpan : 0.0;
                var up = rightSpan > 0 ? (fRight - f) / rightSpan : 0.0;
                var w = Math.Max(0.0, Math.Min(down, up));
                if (w > 0.0)
                {
                    if (start < 0)
                    {
                        start = k;
                    }

                    end = k;
                    dense[k] = w;
                }
            }

            if (start < 0)
            {
                bands[m] = new SparseMelBand(0, []);
            }
            else
            {
                var len = end - start + 1;
                var weights = new double[len];
                Array.Copy(dense, start, weights, 0, len);
                bands[m] = new SparseMelBand(start, weights);
            }
        }

        return bands;
    }

    private static int[] BuildBitReversal(int n)
    {
        var bits = (int)Math.Log2(n);
        var table = new int[n];
        for (var i = 0; i < n; i++)
        {
            var x = i;
            var rev = 0;
            for (var b = 0; b < bits; b++)
            {
                rev = (rev << 1) | (x & 1);
                x >>= 1;
            }

            table[i] = rev;
        }

        return table;
    }

    private static double[] BuildTwiddleCos(int n)
    {
        var cos = new double[n / 2];
        for (var k = 0; k < cos.Length; k++)
        {
            cos[k] = Math.Cos(-2.0 * Math.PI * k / n);
        }

        return cos;
    }

    private static double[] BuildTwiddleSin(int n)
    {
        var sin = new double[n / 2];
        for (var k = 0; k < sin.Length; k++)
        {
            sin[k] = Math.Sin(-2.0 * Math.PI * k / n);
        }

        return sin;
    }

    /// <summary>1024 点原位基-2 Cooley-Tukey 前向 FFT（未归一化，由外层统一除以 sqrt(N)）。</summary>
    private static void FftInPlace(double[] re, double[] im)
    {
        for (var i = 0; i < FftSize; i++)
        {
            var j = BitReversal[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var size = 2; size <= FftSize; size <<= 1)
        {
            var half = size >> 1;
            var step = FftSize / size;
            for (var start = 0; start < FftSize; start += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var twIdx = k * step;
                    var wr = TwiddleCos[twIdx];
                    var wi = TwiddleSin[twIdx];

                    var even = start + k;
                    var odd = even + half;
                    var tr = wr * re[odd] - wi * im[odd];
                    var ti = wr * im[odd] + wi * re[odd];

                    re[odd] = re[even] - tr;
                    im[odd] = im[even] - ti;
                    re[even] += tr;
                    im[even] += ti;
                }
            }
        }
    }
}
