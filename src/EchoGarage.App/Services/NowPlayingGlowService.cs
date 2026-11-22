using System.Windows.Media;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Services;

/// <summary>
/// 「正在播放」封面柔光的取色服务：把封面图缩到 64px 采样，提取主色 / 次色并映射到当前主题的
/// 亮度与彩度区间。原始色按封面缓存键（路径 + 修改戳 + 大小）缓存，切主题只重做映射。
/// 解码后的 BitmapSource 已 Freeze，可安全地在后台线程 CopyPixels。
/// </summary>
public sealed class NowPlayingGlowService
{
    private const int MaxCachedPalettes = 96;

    private readonly IAppLogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<string, RawCoverPalette?> _rawPalettes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _recentKeys = new();

    public NowPlayingGlowService(IAppLogger logger) => _logger = logger;

    /// <summary>为一张新封面取色。返回的颜色已按当前主题归一化。</summary>
    public async Task<CoverPalette> CreateAsync(BitmapSource cover, string cacheKey, CancellationToken cancellationToken = default)
    {
        var raw = await Task.Run(() => GetOrExtract(cover, cacheKey), cancellationToken).ConfigureAwait(false);
        return CoverPaletteThemeMapper.Map(raw, CreateProfile());
    }

    /// <summary>主题切换后只重做「原始色 → 当前主题」的映射；未缓存时返回 null，由调用方重新取色。</summary>
    public CoverPalette? Remap(string cacheKey)
    {
        lock (_gate)
        {
            if (!_rawPalettes.TryGetValue(cacheKey, out var raw))
            {
                return null;
            }

            Touch(cacheKey);
            return CoverPaletteThemeMapper.Map(raw, CreateProfile());
        }
    }

    /// <summary>
    /// 封面缺失 / 解码失败 / 近乎灰度时使用的回退色：用当前主题强调色派生，避免页面完全失去柔光。
    /// </summary>
    public CoverPalette CreateFallback() => CoverPaletteThemeMapper.Map(null, CreateProfile());

    private RawCoverPalette? GetOrExtract(BitmapSource cover, string cacheKey)
    {
        lock (_gate)
        {
            if (_rawPalettes.TryGetValue(cacheKey, out var cached))
            {
                Touch(cacheKey);
                return cached;
            }
        }

        RawCoverPalette? raw = null;
        try
        {
            raw = Extract(cover);
        }
        catch (Exception ex)
        {
            _logger.Debug($"Now playing glow extraction failed: {ex.Message}");
        }

        lock (_gate)
        {
            // 同一张封面的并发请求可能都已计算完；保留先写入的结果即可（确定性算法，值相同）。
            if (!_rawPalettes.ContainsKey(cacheKey))
            {
                _rawPalettes[cacheKey] = raw;
                _recentKeys.AddFirst(cacheKey);
                while (_rawPalettes.Count > MaxCachedPalettes && _recentKeys.Last is { } oldest)
                {
                    _rawPalettes.Remove(oldest.Value);
                    _recentKeys.RemoveLast();
                }
            }
        }

        return raw;
    }

    /// <summary>
    /// 取色只需要长边约 64px 的样本。先在后台线程把 420px 的封面缩小再 CopyPixels：
    /// 否则每切一首歌都要分配一整张 420×420×4（约 0.7 MB，直接进 LOH）的临时缓冲，
    /// 持续切歌时会造成 GC 抖动，拖慢封面过渡。
    /// </summary>
    private const int SampleLongEdge = 64;

    private static RawCoverPalette? Extract(BitmapSource cover)
    {
        if (cover.PixelWidth < 16 || cover.PixelHeight < 16)
        {
            return null;
        }

        BitmapSource source = cover;
        var longEdge = Math.Max(cover.PixelWidth, cover.PixelHeight);
        if (longEdge > SampleLongEdge)
        {
            var scale = SampleLongEdge / (double)longEdge;
            var transform = new ScaleTransform(scale, scale);
            transform.Freeze();
            var scaled = new TransformedBitmap(cover, transform);
            scaled.Freeze();
            source = scaled;
        }

        if (source.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        return CoverPaletteExtractor.Extract(pixels, width, height, stride);
    }

    private static CoverGlowProfile CreateProfile()
    {
        var accent = ThemeManager.AccentColor;
        var fallback = new RgbColor(accent.R, accent.G, accent.B);
        return ThemeManager.IsDark
            ? CoverGlowProfile.ForDarkTheme(fallback)
            : CoverGlowProfile.ForLightTheme(fallback);
    }

    private void Touch(string cacheKey)
    {
        if (_recentKeys.First is { } first && string.Equals(first.Value, cacheKey, StringComparison.Ordinal))
        {
            return;
        }

        if (_recentKeys.Find(cacheKey) is { } node)
        {
            _recentKeys.Remove(node);
        }

        _recentKeys.AddFirst(cacheKey);
    }
}
