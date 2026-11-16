using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Abstractions;

namespace EchoGarage.App.Views;

/// <summary>
/// 封面大图浏览窗口。双击右栏“正在播放”封面打开（非模态、由主窗口拥有）：
/// 滚轮缩放（0.25x–4x，以光标为锚）、拖拽平移、双击或“适应窗口”复位、Esc / “关闭”退出。
/// 浏览期间订阅 <see cref="MainViewModel.PropertyChanged"/> 同步正在播放封面，关闭时退订。
/// </summary>
public partial class CoverViewerWindow : ThemedWindow
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 4.0;
    private const double ZoomStep = 1.25;
    private const double ZoomEpsilon = 0.0001;

    /// <summary>大图解码宽度：1200px 在 150% / 200% DPI 下仍足够清晰，单张冻结位图内存可控。</summary>
    private const int LargeCoverDecodeWidth = 1200;

    private readonly MainViewModel _vm;
    private readonly ICoverCache? _covers;
    private readonly IAppLogger _logger;

    /// <summary>当前显示在视口里的回退图（右栏使用的 420px 解码）；高清大图替换后仍保留该引用用于比对。</summary>
    private BitmapSource? _displayedCover;

    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;
    private bool _dragging;
    private bool _closed;

    private Point _dragOrigin;
    private double _dragOriginOffsetX;
    private double _dragOriginOffsetY;

    /// <summary>每次正在播放封面变化递增，用来丢弃过期的异步大图解码结果。</summary>
    private int _coverVersion;

    /// <summary>已经请求（或已经显示）高清大图对应的封面标识，避免重复解码。</summary>
    private string? _largeCoverKey;

    public CoverViewerWindow(MainViewModel viewModel, ICoverCache? covers, IAppLogger logger)
    {
        _vm = viewModel;
        _covers = covers;
        _logger = logger;

        InitializeComponent();

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        RefreshFromViewModel();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnClosed(e);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CurrentCover)
            or nameof(MainViewModel.IsNowPlayingCoverLoading)
            or nameof(MainViewModel.CurrentRow)
            or nameof(MainViewModel.CurrentTitle)
            or nameof(MainViewModel.CurrentSubtitle))
        {
            RefreshFromViewModel();
        }
    }

    /// <summary>把窗口内容同步到视图模型的当前播放状态；只有封面对象变化时才重置缩放/平移。</summary>
    private void RefreshFromViewModel()
    {
        TrackTitleText.Text = string.IsNullOrWhiteSpace(_vm.CurrentTitle) ? "未在播放" : _vm.CurrentTitle;
        TrackSubtitleText.Text = _vm.CurrentSubtitle ?? string.Empty;

        var cover = _vm.CurrentCover;
        if (!ReferenceEquals(cover, _displayedCover))
        {
            _displayedCover = cover;
            CoverImage.Source = cover;
            _largeCoverKey = null;
            ResetView();
            if (cover is not null)
            {
                RequestLargeCover();
            }
        }

        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        if (_displayedCover is not null)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyHint.Visibility = Visibility.Visible;
        EmptyHint.Text = _vm.IsNowPlayingCoverLoading
            ? "正在加载封面…"
            : _vm.HasCurrentTrack
                ? "当前歌曲没有可显示的封面"
                : "未在播放";
    }

    /// <summary>
    /// 请求解码当前封面的高清版本。右栏只按 420px 解码，放大到 4x 时会明显发虚；
    /// 这里直接从封面缓存文件（原始分辨率）异步解码大图，不写入 CoverImageService 的 LRU。
    /// </summary>
    private void RequestLargeCover()
    {
        var track = _vm.CurrentRow?.Track;
        if (_covers is null || track is null || string.IsNullOrWhiteSpace(track.CoverKey))
        {
            return;
        }

        var coverKey = track.CoverKey;
        var key = $"{coverKey}|{track.ModifiedAtUtc.Ticks}|{track.FileSize}";
        if (string.Equals(_largeCoverKey, key, StringComparison.Ordinal))
        {
            return;
        }

        _largeCoverKey = key;
        var version = ++_coverVersion;
        _ = LoadLargeCoverAsync(coverKey, version);
    }

    /// <summary>
    /// 异步解码高清封面；失败时保留 420px 回退图并写日志，不影响浏览。
    /// 过期结果（切歌 / 窗口关闭）通过版本号丢弃，不会覆盖新封面。
    /// </summary>
    private async Task LoadLargeCoverAsync(string coverKey, int version)
    {
        try
        {
            var image = await Task.Run(() => TryDecodeLargeCover(coverKey));
            if (image is null)
            {
                _logger.Debug($"Cover viewer: no large cover available for '{coverKey}'.");
                return;
            }

            if (_closed || version != _coverVersion || _displayedCover is null)
            {
                return;
            }

            // 大图与回退图宽高比一致，适应窗口尺寸不变，因此保留用户当前的缩放与平移。
            CoverImage.Source = image;
            ClampOffsets();
        }
        catch (Exception ex)
        {
            _logger.Warn($"Cover viewer failed to decode a large cover for '{coverKey}': {ex.Message}");
        }
    }

    private BitmapSource? TryDecodeLargeCover(string coverKey)
    {
        var path = _covers?.Resolve(coverKey);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.DecodePixelWidth = LargeCoverDecodeWidth;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) => ClampOffsets();

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CoverImage.Source is null)
        {
            e.Handled = true;
            return;
        }

        var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep;
        ZoomTo(_zoom * factor, e.GetPosition(Viewport));
        e.Handled = true;
    }

    private void OnViewportMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CoverImage.Source is null)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            ResetView();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragOrigin = e.GetPosition(Viewport);
        _dragOriginOffsetX = _offsetX;
        _dragOriginOffsetY = _offsetY;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var position = e.GetPosition(Viewport);
        _offsetX = _dragOriginOffsetX + (position.X - _dragOrigin.X);
        _offsetY = _dragOriginOffsetY + (position.Y - _dragOrigin.Y);
        ClampOffsets();
        e.Handled = true;
    }

    private void OnViewportMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void OnViewportLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (Viewport.IsMouseCaptured)
        {
            Viewport.ReleaseMouseCapture();
        }

        Viewport.Cursor = Cursors.Arrow;
    }

    private void OnFitToWindowClick(object sender, RoutedEventArgs e) => ResetView();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomToCenter(_zoom * ZoomStep);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomToCenter(_zoom / ZoomStep);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void ZoomToCenter(double targetZoom)
    {
        if (CoverImage.Source is null)
        {
            return;
        }

        ZoomTo(targetZoom, new Point(Viewport.ActualWidth / 2.0, Viewport.ActualHeight / 2.0));
    }

    /// <summary>
    /// 缩放到 <paramref name="targetZoom"/>，并保持 <paramref name="anchor"/>（视口坐标）下的图片内容不动。
    /// 设 k = 新缩放 / 旧缩放、d = 锚点相对视口中心的位移，则平移量 t' = k·t + (1-k)·d。
    /// </summary>
    private void ZoomTo(double targetZoom, Point anchor)
    {
        var newZoom = Math.Clamp(targetZoom, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - _zoom) < ZoomEpsilon)
        {
            return;
        }

        var viewportWidth = Viewport.ActualWidth;
        var viewportHeight = Viewport.ActualHeight;
        if (viewportWidth > 0 && viewportHeight > 0)
        {
            var ratio = newZoom / _zoom;
            var deltaX = anchor.X - viewportWidth / 2.0;
            var deltaY = anchor.Y - viewportHeight / 2.0;
            _offsetX = ratio * _offsetX + (1.0 - ratio) * deltaX;
            _offsetY = ratio * _offsetY + (1.0 - ratio) * deltaY;
        }

        _zoom = newZoom;
        ClampOffsets();
    }

    private void ResetView()
    {
        _zoom = 1.0;
        _offsetX = 0;
        _offsetY = 0;
        UpdateTransform();
    }

    /// <summary>
    /// 限制平移范围：内容在某个方向小于视口时强制居中（偏移 0），否则图片边缘不允许离开视口。
    /// </summary>
    private void ClampOffsets()
    {
        var (contentWidth, contentHeight) = GetFitContentSize();
        _offsetX = ClampAxis(_offsetX, contentWidth * _zoom, Viewport.ActualWidth);
        _offsetY = ClampAxis(_offsetY, contentHeight * _zoom, Viewport.ActualHeight);
        UpdateTransform();
    }

    private static double ClampAxis(double offset, double scaledContentLength, double viewportLength)
    {
        if (viewportLength <= 0 || scaledContentLength <= viewportLength)
        {
            return 0;
        }

        var limit = (scaledContentLength - viewportLength) / 2.0;
        return Math.Clamp(offset, -limit, limit);
    }

    /// <summary>在 Stretch=Uniform、缩放 100% 时图片内容在视口中的显示尺寸（DIP）。</summary>
    private (double Width, double Height) GetFitContentSize()
    {
        if (CoverImage.Source is not BitmapSource bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            return (0, 0);
        }

        var viewportWidth = Viewport.ActualWidth;
        var viewportHeight = Viewport.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return (0, 0);
        }

        var fitScale = Math.Min(viewportWidth / bitmap.PixelWidth, viewportHeight / bitmap.PixelHeight);
        return (bitmap.PixelWidth * fitScale, bitmap.PixelHeight * fitScale);
    }

    private void UpdateTransform()
    {
        CoverScale.ScaleX = _zoom;
        CoverScale.ScaleY = _zoom;
        CoverTranslate.X = _offsetX;
        CoverTranslate.Y = _offsetY;
        ZoomText.Text = $"{(int)Math.Round(_zoom * 100.0)}%";
    }
}
