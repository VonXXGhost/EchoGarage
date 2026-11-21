using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// sRGB ↔ OKLab/OKLCH 转换。<a href="https://bottosson.github.io/posts/oklab/">Björn Ottosson 的 OKLab</a>
/// 是感知均匀色彩空间：做「亮度 / 彩度」归一化时不会像 HSL 那样，黄色与蓝色的同 L 值看起来差很多。
/// </summary>
public static class OklchColorMath
{
    /// <summary>sRGB（0–255）转 OKLCH。</summary>
    public static OklchColor FromRgb(RgbColor color) =>
        FromLinear(ToLinear(color.R / 255.0), ToLinear(color.G / 255.0), ToLinear(color.B / 255.0));

    /// <summary>sRGB（0–255）直转 OKLCH，供逐像素采样使用。</summary>
    public static OklchColor FromSrgbBytes(byte r, byte g, byte b) =>
        FromLinear(ToLinear(r / 255.0), ToLinear(g / 255.0), ToLinear(b / 255.0));

    private static OklchColor FromLinear(double r, double g, double b)
    {
        var l = (0.4122214708 * r) + (0.5363325363 * g) + (0.0514459929 * b);
        var m = (0.2119034982 * r) + (0.6806995451 * g) + (0.1073969566 * b);
        var s = (0.0883024619 * r) + (0.2817188376 * g) + (0.6299787005 * b);

        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);

        var lightness = (0.2104542553 * lRoot) + (0.7936177850 * mRoot) - (0.0040720468 * sRoot);
        var a = (1.9779984951 * lRoot) - (2.4285922050 * mRoot) + (0.4505937099 * sRoot);
        var bAxis = (0.0259040371 * lRoot) + (0.7827717662 * mRoot) - (0.8086757660 * sRoot);

        var chroma = Math.Sqrt((a * a) + (bAxis * bAxis));
        var hue = Math.Atan2(bAxis, a) * 180.0 / Math.PI;
        if (hue < 0)
        {
            hue += 360.0;
        }

        return new OklchColor(lightness, chroma, hue);
    }

    /// <summary>
    /// OKLCH 转 sRGB（0–255）。超出 sRGB 色域时逐步降低彩度而不是直接截断通道，
    /// 直接截断会把色相拉偏（高饱和绿 / 青最容易出现）。
    /// </summary>
    public static RgbColor ToRgb(OklchColor color)
    {
        var chroma = Math.Max(0, color.C);
        var hueRadians = color.H * Math.PI / 180.0;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            if (TryToLinear(color.L, chroma, hueRadians, out var linearR, out var linearG, out var linearB))
            {
                return new RgbColor(
                    ToSrgbByte(linearR),
                    ToSrgbByte(linearG),
                    ToSrgbByte(linearB));
            }

            chroma *= 0.92;
        }

        // 极端输入（L 越界）时退回灰阶，保证永远有合法输出。
        return ToGrayscaleByte(color.L);
    }

    private static bool TryToLinear(double lightness, double chroma, double hueRadians,
        out double r, out double g, out double b)
    {
        var a = chroma * Math.Cos(hueRadians);
        var bAxis = chroma * Math.Sin(hueRadians);

        var lRoot = lightness + (0.3963377774 * a) + (0.2158037573 * bAxis);
        var mRoot = lightness - (0.1055613458 * a) - (0.0638541728 * bAxis);
        var sRoot = lightness - (0.0894841775 * a) - (1.2914855480 * bAxis);

        var l = lRoot * lRoot * lRoot;
        var m = mRoot * mRoot * mRoot;
        var s = sRoot * sRoot * sRoot;

        r = (4.0767416621 * l) - (3.3077115913 * m) + (0.2309699292 * s);
        g = (-1.2684380046 * l) + (2.6097574011 * m) - (0.3413193965 * s);
        b = (-0.0041960863 * l) - (0.7034186147 * m) + (1.7076147010 * s);

        return InGamut(r) && InGamut(g) && InGamut(b);
    }

    private static bool InGamut(double channel) => channel is >= -0.0001 and <= 1.0001;

    private static double ToLinear(double srgb)
    {
        var c = Math.Clamp(srgb, 0.0, 1.0);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static byte ToSrgbByte(double linear)
    {
        var c = Math.Clamp(linear, 0.0, 1.0);
        var srgb = c <= 0.0031308 ? 12.92 * c : (1.055 * Math.Pow(c, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp((int)Math.Round(srgb * 255.0), 0, 255);
    }

    private static RgbColor ToGrayscaleByte(double lightness)
    {
        var value = (byte)Math.Clamp((int)Math.Round(Math.Clamp(lightness, 0.0, 1.0) * 255.0), 0, 255);
        return new RgbColor(value, value, value);
    }
}
