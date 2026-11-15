using System.Globalization;

namespace EchoGarage.Core.Services;

/// <summary>
/// 分段频谱图的纯函数与口径（ADR-023 §6）：频段边界、dB 换算、斜率补偿、频率标签。
/// 只做数学，不碰 NAudio / WPF；Infrastructure 的分析器与 App 的渲染层共用同一套口径。
/// </summary>
public static class SpectrumMath
{
    /// <summary>斜率补偿的参考频率（Hz）：1 kHz 处不增不减。</summary>
    public const double SlopeReferenceHz = 1000.0;

    /// <summary>柱高展示的上限（dBFS）。</summary>
    public const double DefaultCeilingDb = 0.0;

    /// <summary>dB 下限（低于它的幅度一律按该值处理，避免 log10(0)）。</summary>
    public const double MinimumAmplitudeDb = -160.0;

    /// <summary>
    /// 按几何级数生成 N+1 个频段边界：f_i = f_min · (f_max / f_min)^(i/N)。
    /// 人耳对频率是对数感知，等比划分（每段约 1.17 倍，与参考实现一致）比线性划分更接近“等响”。
    /// </summary>
    public static double[] CreateBandEdges(double minHz, double maxHz, int bandCount)
    {
        var count = Math.Max(1, bandCount);
        var low = Math.Max(1.0, minHz);
        var high = Math.Max(low * 1.01, maxHz);
        var ratio = Math.Pow(high / low, 1.0 / count);
        var edges = new double[count + 1];
        edges[0] = low;
        for (var i = 1; i < count; i++)
        {
            edges[i] = edges[i - 1] * ratio;
        }

        edges[count] = high;
        return edges;
    }

    /// <summary>线性幅度转 dBFS；0 与负值落到 <see cref="MinimumAmplitudeDb"/>。</summary>
    public static double AmplitudeToDb(double amplitude) =>
        amplitude > 1e-9 ? Math.Max(MinimumAmplitudeDb, 20.0 * Math.Log10(amplitude)) : MinimumAmplitudeDb;

    /// <summary>把 dBFS 映射为 0..1 的柱高：floorDb 贴地、ceilingDb 满高。</summary>
    public static double Normalize(double db, double floorDb, double ceilingDb)
    {
        if (!(ceilingDb > floorDb))
        {
            return 0.0;
        }

        return Math.Clamp((db - floorDb) / (ceilingDb - floorDb), 0.0, 1.0);
    }

    /// <summary>
    /// 频率斜率的增益（dB）：正斜率按倍频程抬高高频，用来补偿音乐 / 粉噪随频率下降的能量密度。
    /// 参考频率以下为负、以上为正。
    /// </summary>
    public static double SlopeGainDb(double frequencyHz, double dbPerOctave)
    {
        if (Math.Abs(dbPerOctave) < 1e-9 || !(frequencyHz > 0))
        {
            return 0.0;
        }

        return dbPerOctave * Math.Log2(frequencyHz / SlopeReferenceHz);
    }

    /// <summary>频段中心频率（几何中值）：斜率补偿按它算。</summary>
    public static double BandCenter(double lowerHz, double upperHz) =>
        lowerHz > 0 && upperHz > 0 ? Math.Sqrt(lowerHz * upperHz) : 0.0;

    /// <summary>按 dB/s 换算一帧应下落的 dB 数（与帧率无关）。</summary>
    public static double DecayDbPerFrame(double dbPerSecond, double dtSeconds) =>
        Math.Max(0.0, dbPerSecond) * Math.Clamp(dtSeconds, 0.0, 1.0);

    /// <summary>把频率格式化成底部标签："50"、"440"、"1.2k"、"18k"。</summary>
    public static string FormatFrequencyLabel(double frequencyHz)
    {
        if (!(frequencyHz > 0))
        {
            return "0";
        }

        if (frequencyHz < 1000)
        {
            return Math.Round(frequencyHz).ToString("0", CultureInfo.InvariantCulture);
        }

        var kilo = frequencyHz / 1000.0;
        return kilo < 10
            ? kilo.ToString("0.#", CultureInfo.InvariantCulture) + "k"
            : Math.Round(kilo).ToString("0", CultureInfo.InvariantCulture) + "k";
    }
}
