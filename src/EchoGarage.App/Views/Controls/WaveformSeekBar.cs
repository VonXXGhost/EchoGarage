using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views.Controls;

/// <summary>Payload for the preview/commit events raised by <see cref="WaveformSeekBar"/>.</summary>
public sealed class WaveformSeekEventArgs(double seconds) : EventArgs
{
    /// <summary>Target position in seconds, already clamped to the track duration.</summary>
    public double Seconds { get; } = seconds;
}

/// <summary>
/// ADR-015 的整曲波形进度条：平底三层（低频 / 中频 / 高频）不透明堆叠，点击或拖动即跳转。
/// 控件只负责绘制与指针状态机，Seek 本身由视图订阅 <see cref="SeekStarted"/> /
/// <see cref="SeekPreview"/> / <see cref="SeekCommitted"/> / <see cref="SeekCancelled"/> 转交给视图模型。
/// </summary>
public sealed class WaveformSeekBar : FrameworkElement
{
    /// <summary>缩小到 0..255 的列高度，2049 列左右一首 4 分钟曲目。</summary>
    public static readonly DependencyProperty PeaksProperty = DependencyProperty.Register(
        nameof(Peaks), typeof(WaveformPeaks), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnGeometryAffectingPropertyChanged));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(
        nameof(Position), typeof(double), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MidBrushProperty = DependencyProperty.Register(
        nameof(MidBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighBrushProperty = DependencyProperty.Register(
        nameof(HighBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayedOverlayBrushProperty = DependencyProperty.Register(
        nameof(PlayedOverlayBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(
        nameof(PlayheadBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FocusBrushProperty = DependencyProperty.Register(
        nameof(FocusBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    // 每个频段一个 GeometryGroup，内部按设备像素列存放已冻结的矩形 figure。
    // 绘制时一次 DrawGeometry 就完成整层，播放位置刷新不会重建几何。
    private readonly GeometryGroup _lowGeometry = new();
    private readonly GeometryGroup _midGeometry = new();
    private readonly GeometryGroup _highGeometry = new();

    private int _geometryDeviceWidth = -1;
    private int _geometryDeviceHeight = -1;
    private double _geometryDpiScale;
    private int _geometryColumnCount = -1;
    private int _geometryValidCount = -1;
    private double _geometryFloorDb = double.NaN;
    private double _geometryCeilingDb = double.NaN;

    private double _dpiScale = 1;
    private Point _hoverPoint;
    private bool _hasHover;
    private double _previewSeconds = double.NaN;
    private bool _isSeeking;
    private bool _seekCancelled;

    public WaveformSeekBar()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        UseLayoutRounding = true;
    }

    public WaveformPeaks? Peaks
    {
        get => (WaveformPeaks?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Position
    {
        get => (double)GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double Duration
    {
        get => (double)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public Brush? LowBrush
    {
        get => (Brush?)GetValue(LowBrushProperty);
        set => SetValue(LowBrushProperty, value);
    }

    public Brush? MidBrush
    {
        get => (Brush?)GetValue(MidBrushProperty);
        set => SetValue(MidBrushProperty, value);
    }

    public Brush? HighBrush
    {
        get => (Brush?)GetValue(HighBrushProperty);
        set => SetValue(HighBrushProperty, value);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? PlayedOverlayBrush
    {
        get => (Brush?)GetValue(PlayedOverlayBrushProperty);
        set => SetValue(PlayedOverlayBrushProperty, value);
    }

    public Brush? PlayheadBrush
    {
        get => (Brush?)GetValue(PlayheadBrushProperty);
        set => SetValue(PlayheadBrushProperty, value);
    }

    public Brush? HoverBrush
    {
        get => (Brush?)GetValue(HoverBrushProperty);
        set => SetValue(HoverBrushProperty, value);
    }

    public Brush? FocusBrush
    {
        get => (Brush?)GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    /// <summary>True between the initial press and the commit/cancel of a pointer gesture.</summary>
    public bool IsSeeking => _isSeeking;

    /// <summary>Raised on pointer-down: the owner should freeze position updates (BeginSeek).</summary>
    public event EventHandler? SeekStarted;

    /// <summary>Raised while dragging / hovering with the button down; do not seek yet.</summary>
    public event EventHandler<WaveformSeekEventArgs>? SeekPreview;

    /// <summary>Raised exactly once per gesture (mouse-up or lost capture) with the final position.</summary>
    public event EventHandler<WaveformSeekEventArgs>? SeekCommitted;

    /// <summary>Raised when the gesture was abandoned (Escape).</summary>
    public event EventHandler? SeekCancelled;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsNaN(Height) ? 28 : Height;
        return new Size(availableSize.Width, height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 2, 2);
        InvalidateGeometry();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        InvalidateGeometry();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _dpiScale = dpiScale > 0 ? dpiScale : 1;
        var deviceWidth = Math.Max(1, (int)Math.Ceiling(width * _dpiScale));
        var deviceHeight = Math.Max(1, (int)Math.Ceiling(height * _dpiScale));

        var peaks = Peaks;
        var bounds = new Rect(0, 0, width, height);

        // 轨道底色：无曲目时只留一条淡化底，避免传输条里出现突兀的深色块。
        if (TrackBrush is { } trackBrush)
        {
            if (peaks is null)
            {
                dc.PushOpacity(0.3);
                dc.DrawRoundedRectangle(trackBrush, null, bounds, 2, 2);
                dc.Pop();
            }
            else
            {
                dc.DrawRoundedRectangle(trackBrush, null, bounds, 2, 2);
            }
        }

        if (peaks is null || peaks.ColumnCount <= 0)
        {
            DrawFocusAdorner(dc, width, height);
            return;
        }

        EnsureGeometry(deviceWidth, deviceHeight, peaks);

        DrawBand(dc, _lowGeometry, LowBrush);
        DrawBand(dc, _midGeometry, MidBrush);
        DrawBand(dc, _highGeometry, HighBrush);

        var validColumns = ClampValidCount(peaks);
        var analyzedDeviceWidth = (int)Math.Round((double)validColumns / peaks.ColumnCount * deviceWidth);

        // 未分析区域：2 px 基线 + 前缘竖线，提示“还在算”，不转圈也不闪烁。
        if (validColumns < peaks.ColumnCount && TrackBrush is { } pendingBrush && analyzedDeviceWidth < deviceWidth)
        {
            dc.DrawRectangle(pendingBrush, null, DeviceRect(analyzedDeviceWidth, deviceHeight - 2, deviceWidth - analyzedDeviceWidth, 2));
            dc.PushOpacity(0.45);
            dc.DrawRectangle(pendingBrush, null, DeviceRect(analyzedDeviceWidth, 0, 1, deviceHeight));
            dc.Pop();
        }

        // 已播放遮罩：面板色半透明覆盖，保留色相，只压暗。
        if (PlayedOverlayBrush is { } overlay && Duration > 0)
        {
            var playhead = PlayheadDeviceX(deviceWidth);
            if (playhead > 0)
            {
                dc.DrawRectangle(overlay, null, DeviceRect(0, 0, playhead, deviceHeight));
            }
        }

        // 悬停 / 拖动预览线。
        if (_isSeeking && !double.IsNaN(_previewSeconds) && Duration > 0)
        {
            var previewX = SecondsToDeviceX(_previewSeconds, deviceWidth);
            var brush = PlayheadBrush;
            if (brush is not null)
            {
                dc.PushOpacity(0.85);
                dc.DrawRectangle(brush, null, DeviceRect(previewX, 0, 1.5, deviceHeight));
                dc.Pop();
            }
        }
        else if (_hasHover && HoverBrush is { } hoverBrush)
        {
            var hoverX = Math.Clamp((int)Math.Round(_hoverPoint.X * _dpiScale), 0, deviceWidth - 1);
            dc.PushOpacity(0.5);
            dc.DrawRectangle(hoverBrush, null, DeviceRect(hoverX, 0, 1, deviceHeight));
            dc.Pop();
        }

        // 播放头：1.5 设备像素，右端钳制在 W-1 以内。
        if (PlayheadBrush is { } playheadBrush && Duration > 0)
        {
            var playhead = PlayheadDeviceX(deviceWidth);
            dc.DrawRectangle(playheadBrush, null,
                DeviceRect(Math.Min(playhead, deviceWidth - 1), 0, 1.5, deviceHeight));
        }

        DrawFocusAdorner(dc, width, height);
    }

    private void DrawBand(DrawingContext dc, GeometryGroup geometry, Brush? brush)
    {
        if (brush is null || geometry.Children.Count == 0)
        {
            return;
        }

        dc.DrawGeometry(brush, null, geometry);
    }

    private void DrawFocusAdorner(DrawingContext dc, double width, double height)
    {
        if (!IsKeyboardFocusWithin || FocusBrush is not { } focusBrush)
        {
            return;
        }

        var pen = new Pen(focusBrush, 1 / _dpiScale);
        pen.Freeze();
        var inset = 0.5 / _dpiScale;
        dc.DrawRoundedRectangle(null, pen, new Rect(inset, inset, width - 2 * inset, height - 2 * inset), 2, 2);
    }

    private int PlayheadDeviceX(int deviceWidth)
    {
        if (Duration <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(Position / Duration, 0, 1);
        return Math.Clamp((int)Math.Round(ratio * deviceWidth), 0, deviceWidth - 1);
    }

    private int SecondsToDeviceX(double seconds, int deviceWidth)
    {
        var ratio = Duration <= 0 ? 0 : Math.Clamp(seconds / Duration, 0, 1);
        return Math.Clamp((int)Math.Round(ratio * deviceWidth), 0, deviceWidth - 1);
    }

    private Rect DeviceRect(int x, int y, double width, double height) =>
        new(x / _dpiScale, y / _dpiScale, width / _dpiScale, height / _dpiScale);

    /// <summary>几何缓存的键：列数 / 有效列 / 归一化窗口 / 设备像素尺寸 / dpi。</summary>
    private static void OnGeometryAffectingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WaveformSeekBar)d).InvalidateGeometry();
    }

    private void InvalidateGeometry()
    {
        _geometryDeviceWidth = -1;
        _geometryColumnCount = -1;
        InvalidateVisual();
    }

    private void EnsureGeometry(int deviceWidth, int deviceHeight, WaveformPeaks peaks)
    {
        var windowChanged = _geometryColumnCount != peaks.ColumnCount ||
                            Math.Abs(_geometryFloorDb - peaks.FloorDb) > 0.01 ||
                            Math.Abs(_geometryCeilingDb - peaks.CeilingDb) > 0.01 ||
                            deviceWidth != _geometryDeviceWidth ||
                            deviceHeight != _geometryDeviceHeight ||
                            Math.Abs(_geometryDpiScale - _dpiScale) > 0.001;

        if (windowChanged)
        {
            RebuildGeometry(deviceWidth, deviceHeight, peaks, 0);
            return;
        }

        // 渐进快照增加时只补画尾部：从上一段最后一个设备像素列开始（该列可能被当时的
        // ValidCount 截断过），重叠部分是同色不透明矩形，视觉上无差别。
        var validColumns = ClampValidCount(peaks);
        if (validColumns <= _geometryValidCount)
        {
            return;
        }

        var startColumn = _geometryValidCount <= 0
            ? 0
            : Math.Max(0, (int)((long)_geometryValidCount * deviceWidth / peaks.ColumnCount) - 1);
        AppendGeometry(deviceWidth, deviceHeight, peaks, startColumn);
    }

    private void RebuildGeometry(int deviceWidth, int deviceHeight, WaveformPeaks peaks, int startColumn)
    {
        _lowGeometry.Children.Clear();
        _midGeometry.Children.Clear();
        _highGeometry.Children.Clear();
        _geometryDeviceWidth = deviceWidth;
        _geometryDeviceHeight = deviceHeight;
        _geometryDpiScale = _dpiScale;
        _geometryColumnCount = peaks.ColumnCount;
        _geometryFloorDb = peaks.FloorDb;
        _geometryCeilingDb = peaks.CeilingDb;
        _geometryValidCount = Math.Clamp(peaks.ValidCount, 0, peaks.ColumnCount);
        AppendGeometry(deviceWidth, deviceHeight, peaks, startColumn);
    }

    private void AppendGeometry(int deviceWidth, int deviceHeight, WaveformPeaks peaks, int startColumn)
    {
        var validColumns = ClampValidCount(peaks);
        if (validColumns <= 0)
        {
            return;
        }

        for (var column = Math.Clamp(startColumn, 0, deviceWidth - 1); column < deviceWidth; column++)
        {
            // 设备像素列 → 缓存列区间 [start, end)；缓存列比设备像素少时取最近的一列。
            var start = (int)((long)column * peaks.ColumnCount / deviceWidth);
            var end = (int)((long)(column + 1) * peaks.ColumnCount / deviceWidth);
            if (end <= start)
            {
                end = start + 1;
            }

            if (start >= validColumns)
            {
                break;
            }

            end = Math.Min(end, validColumns);
            var length = end - start;

            var heightByte = length == 1 ? peaks.Height[start] : AggregateHeight(peaks.Height, start, length);
            if (heightByte == 0)
            {
                continue;
            }

            byte lowByte;
            byte midByte;
            byte highByte;
            if (length == 1)
            {
                lowByte = peaks.Low[start];
                midByte = peaks.Mid[start];
                highByte = peaks.High[start];
            }
            else
            {
                (lowByte, midByte, highByte) = AggregateShares(peaks, start, length);
            }

            var heightPx = (int)Math.Round(heightByte / 255.0 * deviceHeight);
            heightPx = Math.Clamp(heightPx, 1, deviceHeight);
            var lowPx = (int)Math.Round(heightPx * (lowByte / 255.0));
            var midPx = (int)Math.Round(heightPx * (midByte / 255.0));
            lowPx = Math.Clamp(lowPx, 0, heightPx);
            midPx = Math.Clamp(midPx, 0, heightPx - lowPx);
            var highPx = heightPx - lowPx - midPx;

            var x = column / _dpiScale;
            var columnWidth = 1 / _dpiScale;
            var y = (double)deviceHeight;

            if (lowPx > 0)
            {
                AddRect(_lowGeometry, x, (y - lowPx) / _dpiScale, columnWidth, lowPx / _dpiScale);
                y -= lowPx;
            }

            if (midPx > 0)
            {
                AddRect(_midGeometry, x, (y - midPx) / _dpiScale, columnWidth, midPx / _dpiScale);
                y -= midPx;
            }

            if (highPx > 0)
            {
                AddRect(_highGeometry, x, (y - highPx) / _dpiScale, columnWidth, highPx / _dpiScale);
            }
        }

        _geometryValidCount = validColumns;
    }

    private static void AddRect(GeometryGroup group, double x, double y, double width, double height)
    {
        var rect = new RectGeometry(x, y, width, height);
        group.Children.Add(rect.Geometry);
    }

    /// <summary>渐进快照可能只带前缀数组，长度与 ColumnCount 不一致时以最短的为准。</summary>
    private static int ClampValidCount(WaveformPeaks peaks)
    {
        var length = Math.Min(peaks.ColumnCount, peaks.Height.Length);
        length = Math.Min(length, peaks.Low.Length);
        length = Math.Min(length, peaks.Mid.Length);
        length = Math.Min(length, peaks.High.Length);
        return Math.Clamp(peaks.ValidCount, 0, Math.Max(0, length));
    }

    /// <summary>段内高度取 p80：max 会保留 1 px 梳齿，mean 会把瞬态抹平。</summary>
    private static byte AggregateHeight(byte[] heights, int start, int length)
    {
        Span<int> histogram = stackalloc int[256];
        for (var i = start; i < start + length; i++)
        {
            histogram[heights[i]]++;
        }

        var target = (int)Math.Ceiling(length * 0.8) - 1;
        if (target < 0)
        {
            target = 0;
        }

        var accumulated = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            accumulated += histogram[value];
            if (accumulated > target)
            {
                return (byte)value;
            }
        }

        return 255;
    }

    /// <summary>份额按 height^1.5 加权平均后重新量化到和 = 255。</summary>
    private static (byte Low, byte Mid, byte High) AggregateShares(WaveformPeaks peaks, int start, int length)
    {
        double lowSum = 0;
        double midSum = 0;
        double highSum = 0;
        double weightSum = 0;

        for (var i = start; i < start + length; i++)
        {
            var weight = Math.Pow(peaks.Height[i], 1.5);
            if (weight <= 0)
            {
                continue;
            }

            lowSum += peaks.Low[i] * weight;
            midSum += peaks.Mid[i] * weight;
            highSum += peaks.High[i] * weight;
            weightSum += weight;
        }

        if (weightSum <= 0)
        {
            return (85, 85, 85);
        }

        var low = lowSum / weightSum;
        var mid = midSum / weightSum;
        var lowByte = (byte)Math.Clamp((int)Math.Round(low), 0, 255);
        var midByte = (byte)Math.Clamp((int)Math.Round(mid), 0, 255 - lowByte);
        return (lowByte, midByte, (byte)(255 - lowByte - midByte));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Duration <= 0 || !IsEnabled)
        {
            return;
        }

        Focus();
        _isSeeking = true;
        _seekCancelled = false;
        CaptureMouse();
        _previewSeconds = PointToSeconds(e.GetPosition(this));
        SeekStarted?.Invoke(this, EventArgs.Empty);
        RaisePreview(_previewSeconds);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var point = e.GetPosition(this);
        _hasHover = true;
        _hoverPoint = point;

        if (_isSeeking && e.LeftButton == MouseButtonState.Pressed)
        {
            _previewSeconds = PointToSeconds(point);
            RaisePreview(_previewSeconds);
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hasHover = false;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isSeeking)
        {
            return;
        }

        var seconds = PointToSeconds(e.GetPosition(this));
        CommitGesture(seconds);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // 与滑块一致：捕获丢失（例如弹窗抢占）时用最后一个预览位置提交，避免手势悬空。
        if (_isSeeking && !_seekCancelled)
        {
            CommitGesture(double.IsNaN(_previewSeconds) ? Position : _previewSeconds);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && _isSeeking)
        {
            _seekCancelled = true;
            _isSeeking = false;
            _previewSeconds = double.NaN;
            SeekCancelled?.Invoke(this, EventArgs.Empty);
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (Duration <= 0 || !IsEnabled)
        {
            return;
        }

        var delta = e.Key switch
        {
            Key.Left => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : -5,
            Key.Right => (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1 : 5,
            Key.PageUp => 30,
            Key.PageDown => -30,
            _ => 0
        };

        double? target = e.Key switch
        {
            Key.Home => 0,
            Key.End => Duration,
            _ => delta != 0 ? Position + delta : null
        };

        if (target is null)
        {
            return;
        }

        var clamped = Math.Clamp(target.Value, 0, Math.Max(Duration, 0));
        SeekCommitted?.Invoke(this, new WaveformSeekEventArgs(clamped));
        e.Handled = true;
    }

    private void CommitGesture(double seconds)
    {
        _isSeeking = false;
        _previewSeconds = double.NaN;
        InvalidateVisual();
        SeekCommitted?.Invoke(this, new WaveformSeekEventArgs(Math.Clamp(seconds, 0, Math.Max(Duration, 0))));
    }

    private void RaisePreview(double seconds) =>
        SeekPreview?.Invoke(this, new WaveformSeekEventArgs(Math.Clamp(seconds, 0, Math.Max(Duration, 0))));

    private double PointToSeconds(Point point)
    {
        if (Duration <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(point.X / Math.Max(ActualWidth, 1), 0, 1);
        return ratio * Duration;
    }

    /// <summary>按需创建的矩形 figure；冻结后可以安全地放进未冻结的 GeometryGroup。</summary>
    private readonly struct RectGeometry
    {
        public RectGeometry(double x, double y, double width, double height)
        {
            var geometry = new RectangleGeometry(new Rect(x, y, width, height));
            geometry.Freeze();
            Geometry = geometry;
        }

        public RectangleGeometry Geometry { get; }
    }
}
