using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// 封面柔光取色的纯函数口径：过滤（近黑 / 近白 / 近灰 / 透明）、主次色选取、
/// 主题归一化与回退。算法本身没有 WPF 依赖，所以都在这里跑。
/// </summary>
public sealed class CoverPaletteTests
{
    private static readonly RgbColor Accent = new(0x25, 0x63, 0xEB);

    [Fact]
    public void Extract_SolidBlue_ReturnsVividPrimaryNearBlueHue()
    {
        var pixels = CreateSolid(64, 64, new RgbColor(37, 99, 235));

        var palette = CoverPaletteExtractor.Extract(pixels, 64, 64, 64 * 4);

        Assert.NotNull(palette);
        Assert.True(palette!.Primary.C > 0.05, $"彩度应足够高，实际 {palette.Primary.C:F3}");
        Assert.InRange(palette.Primary.H, 240.0, 290.0);
        Assert.True(palette.Primary.L is > 0.2 and < 0.8, $"亮度应在中段，实际 {palette.Primary.L:F3}");
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    [InlineData(16, 16, 16)]
    [InlineData(246, 246, 246)]
    [InlineData(128, 128, 128)]
    public void Extract_FilteredColors_ReturnsNull(byte r, byte g, byte b)
    {
        var pixels = CreateSolid(32, 32, new RgbColor(r, g, b));

        var palette = CoverPaletteExtractor.Extract(pixels, 32, 32, 32 * 4);

        Assert.Null(palette);
    }

    [Fact]
    public void Extract_GrayCoverWithTinyLogo_ReturnsNull()
    {
        // 灰度底 + 2×2 红色 logo：logo 占比 0.4%，低于 2% 的有效彩色阈值。
        var pixels = CreateSolid(64, 64, new RgbColor(180, 180, 180));
        FillSquare(pixels, 64, 31, 31, 2, new RgbColor(220, 20, 20));

        var palette = CoverPaletteExtractor.Extract(pixels, 64, 64, 64 * 4);

        Assert.Null(palette);
    }

    [Fact]
    public void Extract_TransparentCover_ReturnsNull()
    {
        var pixels = CreateSolid(32, 32, new RgbColor(220, 40, 40), alpha: 0);

        var palette = CoverPaletteExtractor.Extract(pixels, 32, 32, 32 * 4);

        Assert.Null(palette);
    }

    [Fact]
    public void Extract_TwoToneCover_SecondaryIsVisiblyDifferentHue()
    {
        var pixels = CreateSolid(64, 64, new RgbColor(210, 30, 40));
        for (var y = 0; y < 64; y++)
        {
            for (var x = 32; x < 64; x++)
            {
                WritePixel(pixels, 64, x, y, new RgbColor(30, 60, 210));
            }
        }

        var palette = CoverPaletteExtractor.Extract(pixels, 64, 64, 64 * 4);

        Assert.NotNull(palette);
        var distance = HueDistance(palette!.Primary.H, palette.Secondary.H);
        Assert.True(distance >= 25.0, $"主次色相距离应 ≥25°，实际 {distance:F1}°");
        Assert.True(palette.Primary.C >= 0.04, $"主色彩度不足：{palette.Primary.C:F3}");
        Assert.True(palette.Secondary.C >= 0.02, $"次色彩度不足：{palette.Secondary.C:F3}");
    }

    [Fact]
    public void Extract_IsDeterministic()
    {
        var pixels = CreateSolid(64, 64, new RgbColor(40, 170, 90));
        FillSquare(pixels, 64, 0, 0, 32, new RgbColor(250, 190, 20));

        var first = CoverPaletteExtractor.Extract(pixels, 64, 64, 64 * 4);
        var second = CoverPaletteExtractor.Extract(pixels, 64, 64, 64 * 4);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Primary, second!.Primary);
        Assert.Equal(first.Secondary, second.Secondary);
    }

    [Fact]
    public void Extract_RejectsMalformedBuffers()
    {
        Assert.Null(CoverPaletteExtractor.Extract([], 0, 0, 0));
        Assert.Null(CoverPaletteExtractor.Extract(new byte[16], 8, 8, 8 * 4));
        Assert.Null(CoverPaletteExtractor.Extract(new byte[16], 8, 8, 8));
    }

    [Fact]
    public void Map_NoRawPalette_FallsBackToThemeAccent()
    {
        var profile = CoverGlowProfile.ForLightTheme(Accent);

        var palette = CoverPaletteThemeMapper.Map(null, profile);

        Assert.True(palette.UsedFallback);
        Assert.Equal(Accent, palette.Primary);
        Assert.NotEqual(palette.Primary, palette.Secondary);
    }

    [Fact]
    public void Map_LightTheme_ClampsLightnessAndChromaIntoViewRange()
    {
        var raw = new RawCoverPalette(
            new OklchColor(0.94, 0.22, 250),
            new OklchColor(0.98, 0.18, 30));

        var palette = CoverPaletteThemeMapper.Map(raw, CoverGlowProfile.ForLightTheme(Accent));

        Assert.False(palette.UsedFallback);
        var primary = OklchColorMath.FromRgb(palette.Primary);
        var secondary = OklchColorMath.FromRgb(palette.Secondary);
        Assert.InRange(primary.L, 0.49, 0.69);
        Assert.InRange(primary.C, 0.05, 0.17);
        Assert.InRange(secondary.L, 0.53, 0.73);
        Assert.InRange(secondary.C, 0.03, 0.13);
    }

    [Fact]
    public void Map_DarkTheme_UsesBrighterRange()
    {
        var raw = new RawCoverPalette(
            new OklchColor(0.28, 0.20, 250),
            new OklchColor(0.30, 0.18, 30));

        var palette = CoverPaletteThemeMapper.Map(raw, CoverGlowProfile.ForDarkTheme(Accent));

        var primary = OklchColorMath.FromRgb(palette.Primary);
        var secondary = OklchColorMath.FromRgb(palette.Secondary);
        Assert.InRange(primary.L, 0.59, 0.81);
        Assert.InRange(secondary.L, 0.59, 0.79);
    }

    [Fact]
    public void Extract_StaysFastEnoughForBackgroundUse()
    {
        var pixels = CreateSolid(420, 420, new RgbColor(60, 120, 200));
        FillSquare(pixels, 420, 0, 0, 200, new RgbColor(240, 180, 40));

        _ = CoverPaletteExtractor.Extract(pixels, 420, 420, 420 * 4);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 3; i++)
        {
            _ = CoverPaletteExtractor.Extract(pixels, 420, 420, 420 * 4);
        }

        stopwatch.Stop();
        Assert.True(stopwatch.ElapsedMilliseconds < 600,
            $"三次取色耗时 {stopwatch.ElapsedMilliseconds}ms，超过后台线程可接受范围");
    }

    private static byte[] CreateSolid(int width, int height, RgbColor color, byte alpha = 255)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                WritePixel(pixels, width, x, y, color, alpha);
            }
        }

        return pixels;
    }

    private static void FillSquare(byte[] pixels, int width, int x, int y, int size, RgbColor color)
    {
        for (var dy = 0; dy < size; dy++)
        {
            for (var dx = 0; dx < size; dx++)
            {
                WritePixel(pixels, width, x + dx, y + dy, color);
            }
        }
    }

    private static void WritePixel(byte[] pixels, int width, int x, int y, RgbColor color, byte alpha = 255)
    {
        var offset = ((y * width) + x) * 4;
        pixels[offset] = color.B;
        pixels[offset + 1] = color.G;
        pixels[offset + 2] = color.R;
        pixels[offset + 3] = alpha;
    }

    private static double HueDistance(double first, double second)
    {
        var difference = Math.Abs(first - second) % 360.0;
        return difference > 180.0 ? 360.0 - difference : difference;
    }
}
