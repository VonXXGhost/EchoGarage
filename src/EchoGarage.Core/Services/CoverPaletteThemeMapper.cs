using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 把 <see cref="RawCoverPalette"/> 映射成当前主题能直接用的两束光晕颜色：
/// 浅色主题把亮度压到中段、彩度抬高一点（洗在白色底上才看得见），
/// 深色主题提亮、提高彩度；都没有可用彩色时回退到主题强调色。
/// </summary>
public static class CoverPaletteThemeMapper
{
    /// <summary>回退色（主题强调色）派生次光时的色相偏移。</summary>
    private const double FallbackSecondaryHueShift = 25.0;

    public static CoverPalette Map(RawCoverPalette? raw, CoverGlowProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (raw is null)
        {
            var fallback = OklchColorMath.FromRgb(profile.FallbackPrimary);
            var secondary = new OklchColor(
                Math.Min(0.80, fallback.L + 0.06),
                Math.Max(0.03, fallback.C * 0.75),
                (fallback.H + FallbackSecondaryHueShift) % 360.0);
            return new CoverPalette(
                profile.FallbackPrimary,
                OklchColorMath.ToRgb(secondary),
                UsedFallback: true);
        }

        return new CoverPalette(
            Normalize(raw.Primary, profile.PrimaryLightnessMin, profile.PrimaryLightnessMax,
                profile.PrimaryChromaMin, profile.PrimaryChromaMax, profile.PrimaryChromaGain),
            Normalize(raw.Secondary, profile.SecondaryLightnessMin, profile.SecondaryLightnessMax,
                profile.SecondaryChromaMin, profile.SecondaryChromaMax, profile.SecondaryChromaGain),
            UsedFallback: false);
    }

    private static RgbColor Normalize(
        OklchColor color,
        double lightnessMin,
        double lightnessMax,
        double chromaMin,
        double chromaMax,
        double chromaGain)
    {
        var lightness = Math.Clamp(color.L, lightnessMin, lightnessMax);
        var chroma = Math.Clamp(color.C * chromaGain, chromaMin, chromaMax);
        return OklchColorMath.ToRgb(new OklchColor(lightness, chroma, color.H));
    }
}
