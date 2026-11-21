namespace EchoGarage.Core.Models;

/// <summary>8 位 sRGB 颜色。Core 层不引用 WPF，界面层再换算成 <c>System.Windows.Media.Color</c>。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);

/// <summary>OKLCH 颜色：L 为感知亮度（0–1），C 为彩度，H 为色相角（度）。</summary>
public readonly record struct OklchColor(double L, double C, double H);

/// <summary>
/// 从封面统计出的原始色调（未按界面主题归一化）。<see cref="OklchColor.H"/> 为色相角，
/// 两束颜色保证色相距离 ≥ 25°，避免在浅色界面上糊成一块脏斑。
/// </summary>
public sealed record RawCoverPalette(OklchColor Primary, OklchColor Secondary);

/// <summary>已经按当前主题归一化、可直接用作径向光晕的颜色。</summary>
/// <param name="Primary">主光晕颜色（第一束光）。</param>
/// <param name="Secondary">次光晕颜色（第二束光）。</param>
/// <param name="UsedFallback">封面没有可用彩色时回退到主题强调色。</param>
public sealed record CoverPalette(RgbColor Primary, RgbColor Secondary, bool UsedFallback);

/// <summary>
/// 把封面原始色调映射到当前主题可用区间的参数。浅色主题需要「中等偏深、彩度足够」的颜色，
/// 深色主题需要「更亮、更饱和」的颜色，否则 25% / 13% 透明度洗在底色上会看不见或发脏。
/// </summary>
public sealed record CoverGlowProfile(
    bool IsDark,
    double PrimaryLightnessMin,
    double PrimaryLightnessMax,
    double PrimaryChromaMin,
    double PrimaryChromaMax,
    double SecondaryLightnessMin,
    double SecondaryLightnessMax,
    double SecondaryChromaMin,
    double SecondaryChromaMax,
    RgbColor FallbackPrimary)
{
    /// <summary>浅色底上洗白后彩度衰减明显，主色额外增益。</summary>
    public double PrimaryChromaGain => IsDark ? 1.10 : 1.25;

    public double SecondaryChromaGain => IsDark ? 1.12 : 1.10;

    /// <summary>浅色主题的默认区间（与调研报告的 5 套主题建议一致）。</summary>
    public static CoverGlowProfile ForLightTheme(RgbColor fallbackPrimary) => new(
        IsDark: false,
        PrimaryLightnessMin: 0.52,
        PrimaryLightnessMax: 0.66,
        PrimaryChromaMin: 0.07,
        PrimaryChromaMax: 0.15,
        SecondaryLightnessMin: 0.56,
        SecondaryLightnessMax: 0.70,
        SecondaryChromaMin: 0.05,
        SecondaryChromaMax: 0.11,
        fallbackPrimary);

    /// <summary>深色主题的默认区间：提亮、提高彩度，避免变成一团脏黑。</summary>
    public static CoverGlowProfile ForDarkTheme(RgbColor fallbackPrimary) => new(
        IsDark: true,
        PrimaryLightnessMin: 0.62,
        PrimaryLightnessMax: 0.78,
        PrimaryChromaMin: 0.06,
        PrimaryChromaMax: 0.13,
        SecondaryLightnessMin: 0.62,
        SecondaryLightnessMax: 0.76,
        SecondaryChromaMin: 0.05,
        SecondaryChromaMax: 0.10,
        fallbackPrimary);
}
