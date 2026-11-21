using System.Buffers;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 从封面缩略图的 BGRA32 像素中提取两束「环境色」（主光 / 次光）。管线参考 AndroidX Palette：
/// 采样 → 过滤近黑 / 近白 / 近灰 / 透明像素 → 5-bit RGB 直方图 → 中位切分 16 箱 →
/// 用「彩度 + 亮度目标 + 像素占比」打分选色。ColorThief 的 MMCQ、Palette 的 Target 打分与
/// Material You 的量化思路在本项目里合并成了一个纯函数，不依赖 WPF，可在后台线程调用。
/// <para>
/// 输出是未按主题归一化的 <see cref="RawCoverPalette"/>，主题映射见
/// <see cref="CoverPaletteThemeMapper"/>，这样切换主题时不需要重新解码封面。
/// </para>
/// </summary>
public static class CoverPaletteExtractor
{
    /// <summary>采样后的目标长边像素数。64² ≈ 4096 个样本，约 0.5–1.5 ms。</summary>
    public const int DefaultSampleLongEdge = 64;

    private const int QuantizeBits = 5;
    private const int QuantizeShift = 8 - QuantizeBits;
    private const int BucketCount = 1 << (3 * QuantizeBits);
    private const int MaxBoxes = 16;
    private const byte AlphaThreshold = 128;
    private const double BlackLightness = 0.16;
    private const double WhiteLightness = 0.95;
    private const double GrayChroma = 0.012;
    private const int MinColoredSamples = 32;
    private const double MinColoredFraction = 0.02;
    private const double SecondaryHueDistance = 25.0;

    private static readonly Target PrimaryTarget = new(
        MinChroma: 0.045, MinLightness: 0.34, MaxLightness: 0.80,
        MinPopulationFraction: 0.015, TargetLightness: 0.60);

    private static readonly Target PrimaryRelaxedTarget = new(
        MinChroma: 0.020, MinLightness: 0.25, MaxLightness: 0.92,
        MinPopulationFraction: 0.005, TargetLightness: 0.60);

    private static readonly Target SecondaryTarget = new(
        MinChroma: 0.025, MinLightness: 0.30, MaxLightness: 0.88,
        MinPopulationFraction: 0.005, TargetLightness: 0.70);

    /// <summary>
    /// 提取封面原始色调。无可用彩色（纯黑 / 纯白 / 灰度 / 透明 / 解码失败）时返回 <c>null</c>，
    /// 由调用方回退到主题强调色。
    /// </summary>
    /// <param name="bgra">按 BGRA32 排列、非预乘 alpha 的像素。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <param name="stride">一行字节数，必须 ≥ width * 4。</param>
    /// <param name="sampleLongEdge">采样后长边像素数。</param>
    public static RawCoverPalette? Extract(
        ReadOnlySpan<byte> bgra,
        int width,
        int height,
        int stride,
        int sampleLongEdge = DefaultSampleLongEdge)
    {
        if (width <= 0 || height <= 0 || stride < width * 4)
        {
            return null;
        }

        if (bgra.Length < stride * height)
        {
            return null;
        }

        var sample = Math.Clamp(sampleLongEdge, 8, 128);
        var step = Math.Max(1, Math.Max(width, height) / sample);

        var histogram = ArrayPool<int>.Shared.Rent(BucketCount);
        Array.Clear(histogram, 0, BucketCount);
        try
        {
            var opaqueSamples = 0;
            var coloredSamples = 0;
            for (var y = step / 2; y < height; y += step)
            {
                var rowOffset = y * stride;
                for (var x = step / 2; x < width; x += step)
                {
                    var offset = rowOffset + (x * 4);
                    if (bgra[offset + 3] < AlphaThreshold)
                    {
                        continue;
                    }

                    opaqueSamples++;
                    var r = bgra[offset + 2];
                    var g = bgra[offset + 1];
                    var b = bgra[offset];

                    var color = OklchColorMath.FromSrgbBytes(r, g, b);
                    if (color.L <= BlackLightness || color.L >= WhiteLightness || color.C < GrayChroma)
                    {
                        continue;
                    }

                    coloredSamples++;
                    var bucket = ((r >> QuantizeShift) << (2 * QuantizeBits))
                                 | ((g >> QuantizeShift) << QuantizeBits)
                                 | (b >> QuantizeShift);
                    histogram[bucket]++;
                }
            }

            if (opaqueSamples == 0
                || coloredSamples < MinColoredSamples
                || coloredSamples < (int)(opaqueSamples * MinColoredFraction))
            {
                return null;
            }

            var swatches = BuildSwatches(histogram);
            if (swatches.Count == 0)
            {
                return null;
            }

            var primaryCandidate = Select(swatches, PrimaryTarget, coloredSamples, null, 0)
                                   ?? Select(swatches, PrimaryRelaxedTarget, coloredSamples, null, 0);
            if (primaryCandidate is not { } primary)
            {
                return null;
            }

            var secondary = Select(swatches, SecondaryTarget, coloredSamples, primary, SecondaryHueDistance)
                            ?? DeriveSecondary(primary);

            return new RawCoverPalette(primary.RawColor, secondary.RawColor);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(histogram);
        }
    }

    private static Swatch DeriveSecondary(Swatch primary)
    {
        var hue = (primary.RawColor.H + 18.0) % 360.0;
        var chroma = Math.Max(0.03, primary.RawColor.C * 0.65);
        var lightness = Math.Min(0.80, primary.RawColor.L + 0.06);
        return new Swatch(new OklchColor(lightness, chroma, hue), primary.Population);
    }

    private static List<Swatch> BuildSwatches(int[] histogram)
    {
        var boxes = new List<Box>(MaxBoxes) { CreateBox(histogram, 0, 31, 0, 31, 0, 31) };
        if (boxes[0].Population == 0)
        {
            return [];
        }

        while (boxes.Count < MaxBoxes)
        {
            var splitIndex = -1;
            var bestPopulation = 0;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].IsSplittable && boxes[i].Population > bestPopulation)
                {
                    bestPopulation = boxes[i].Population;
                    splitIndex = i;
                }
            }

            if (splitIndex < 0)
            {
                break;
            }

            if (!TrySplit(histogram, boxes[splitIndex], out var lower, out var upper))
            {
                break;
            }

            boxes[splitIndex] = lower;
            boxes.Add(upper);
        }

        var swatches = new List<Swatch>(boxes.Count);
        foreach (var box in boxes)
        {
            var population = 0;
            double lightnessSum = 0;
            double aSum = 0;
            double bSum = 0;
            for (var binR = box.MinR; binR <= box.MaxR; binR++)
            {
                for (var binG = box.MinG; binG <= box.MaxG; binG++)
                {
                    for (var binB = box.MinB; binB <= box.MaxB; binB++)
                    {
                        var count = histogram[Index(binR, binG, binB)];
                        if (count == 0)
                        {
                            continue;
                        }

                        var bucketR = (byte)((binR << QuantizeShift) + (1 << (QuantizeShift - 1)));
                        var bucketG = (byte)((binG << QuantizeShift) + (1 << (QuantizeShift - 1)));
                        var bucketB = (byte)((binB << QuantizeShift) + (1 << (QuantizeShift - 1)));
                        var color = OklchColorMath.FromSrgbBytes(bucketR, bucketG, bucketB);
                        var hueRadians = color.H * Math.PI / 180.0;

                        population += count;
                        lightnessSum += color.L * count;
                        aSum += color.C * Math.Cos(hueRadians) * count;
                        bSum += color.C * Math.Sin(hueRadians) * count;
                    }
                }
            }

            if (population == 0)
            {
                continue;
            }

            var lightness = lightnessSum / population;
            var a = aSum / population;
            var bAxis = bSum / population;
            var chroma = Math.Sqrt((a * a) + (bAxis * bAxis));
            var hue = Math.Atan2(bAxis, a) * 180.0 / Math.PI;
            if (hue < 0)
            {
                hue += 360.0;
            }

            swatches.Add(new Swatch(new OklchColor(lightness, chroma, hue), population));
        }

        return swatches;
    }

    private static Swatch? Select(
        List<Swatch> swatches,
        Target target,
        int maxPopulation,
        Swatch? forbidden,
        double minHueDistance)
    {
        Swatch? best = null;
        var bestScore = double.NegativeInfinity;
        var minPopulation = Math.Max(1, (int)(maxPopulation * target.MinPopulationFraction));

        foreach (var swatch in swatches)
        {
            if (swatch.Population < minPopulation)
            {
                continue;
            }

            var color = swatch.RawColor;
            if (color.C < target.MinChroma || color.L < target.MinLightness || color.L > target.MaxLightness)
            {
                continue;
            }

            if (forbidden is { } forbiddenSwatch
                && HueDistance(color.H, forbiddenSwatch.RawColor.H) < minHueDistance)
            {
                continue;
            }

            var populationScore = (double)swatch.Population / maxPopulation;
            var chromaScore = Clamp01((color.C - 0.020) / 0.140);
            var lightnessScore = Clamp01(1 - (Math.Abs(color.L - target.TargetLightness) / 0.30));
            var score = (0.30 * populationScore) + (0.45 * chromaScore) + (0.25 * lightnessScore);
            if (score > bestScore)
            {
                bestScore = score;
                best = swatch;
            }
        }

        return best;
    }

    private static double HueDistance(double first, double second)
    {
        var difference = Math.Abs(first - second) % 360.0;
        return difference > 180.0 ? 360.0 - difference : difference;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private static int Index(int r, int g, int b) =>
        (r << (2 * QuantizeBits)) | (g << QuantizeBits) | b;

    private static Box CreateBox(int[] histogram, int minR, int maxR, int minG, int maxG, int minB, int maxB)
    {
        var population = 0;
        for (var r = minR; r <= maxR; r++)
        {
            for (var g = minG; g <= maxG; g++)
            {
                for (var b = minB; b <= maxB; b++)
                {
                    population += histogram[Index(r, g, b)];
                }
            }
        }

        return new Box(minR, maxR, minG, maxG, minB, maxB, population);
    }

    private static bool TrySplit(int[] histogram, Box box, out Box lower, out Box upper)
    {
        lower = default;
        upper = default;

        // 取最长的一维做切分；长度相同优先 R → G → B，保证结果确定。
        var rangeR = box.MaxR - box.MinR;
        var rangeG = box.MaxG - box.MinG;
        var rangeB = box.MaxB - box.MinB;
        var axis = rangeR >= rangeG && rangeR >= rangeB
            ? 0
            : rangeG >= rangeB ? 1 : 2;

        var splitIndex = FindSplitIndex(histogram, box, axis);
        if (splitIndex < 0)
        {
            return false;
        }

        lower = axis switch
        {
            0 => CreateBox(histogram, box.MinR, splitIndex, box.MinG, box.MaxG, box.MinB, box.MaxB),
            1 => CreateBox(histogram, box.MinR, box.MaxR, box.MinG, splitIndex, box.MinB, box.MaxB),
            _ => CreateBox(histogram, box.MinR, box.MaxR, box.MinG, box.MaxG, box.MinB, splitIndex)
        };
        upper = axis switch
        {
            0 => CreateBox(histogram, splitIndex + 1, box.MaxR, box.MinG, box.MaxG, box.MinB, box.MaxB),
            1 => CreateBox(histogram, box.MinR, box.MaxR, splitIndex + 1, box.MaxG, box.MinB, box.MaxB),
            _ => CreateBox(histogram, box.MinR, box.MaxR, box.MinG, box.MaxG, splitIndex + 1, box.MaxB)
        };

        return lower.Population > 0 && upper.Population > 0;
    }

    /// <summary>按目标轴累计像素，找人口刚好过半的那条切片（切在切片之后）。</summary>
    private static int FindSplitIndex(int[] histogram, Box box, int axis)
    {
        var min = axis switch { 0 => box.MinR, 1 => box.MinG, _ => box.MinB };
        var max = axis switch { 0 => box.MaxR, 1 => box.MaxG, _ => box.MaxB };
        if (min >= max)
        {
            return -1;
        }

        var half = box.Population / 2;
        var cumulative = 0;
        var firstNonEmpty = -1;
        for (var slot = min; slot <= max; slot++)
        {
            var slicePopulation = CountSlice(histogram, box, axis, slot);
            if (slicePopulation == 0)
            {
                continue;
            }

            if (firstNonEmpty < 0)
            {
                firstNonEmpty = slot;
            }

            cumulative += slicePopulation;
            if (slot < max && cumulative >= half && cumulative < box.Population)
            {
                return slot;
            }
        }

        // 人口集中在首尾（例如只有两种颜色）时，切第一段之后也能把两种颜色分开。
        if (firstNonEmpty >= min && firstNonEmpty < max)
        {
            return firstNonEmpty;
        }

        return -1;
    }

    private static int CountSlice(int[] histogram, Box box, int axis, int slot)
    {
        var population = 0;
        switch (axis)
        {
            case 0:
                for (var g = box.MinG; g <= box.MaxG; g++)
                {
                    for (var b = box.MinB; b <= box.MaxB; b++)
                    {
                        population += histogram[Index(slot, g, b)];
                    }
                }

                break;
            case 1:
                for (var r = box.MinR; r <= box.MaxR; r++)
                {
                    for (var b = box.MinB; b <= box.MaxB; b++)
                    {
                        population += histogram[Index(r, slot, b)];
                    }
                }

                break;
            default:
                for (var r = box.MinR; r <= box.MaxR; r++)
                {
                    for (var g = box.MinG; g <= box.MaxG; g++)
                    {
                        population += histogram[Index(r, g, slot)];
                    }
                }

                break;
        }

        return population;
    }

    private readonly record struct Swatch(OklchColor RawColor, int Population);

    private readonly record struct Target(
        double MinChroma,
        double MinLightness,
        double MaxLightness,
        double MinPopulationFraction,
        double TargetLightness);

    private readonly record struct Box(
        int MinR,
        int MaxR,
        int MinG,
        int MaxG,
        int MinB,
        int MaxB,
        int Population)
    {
        public bool IsSplittable =>
            Population > 1 && (MaxR > MinR || MaxG > MinG || MaxB > MinB);
    }
}
