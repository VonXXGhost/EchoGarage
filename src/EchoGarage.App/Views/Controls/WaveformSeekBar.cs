using System.Diagnostics;
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
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnPeaksPropertyChanged));

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

    public static readonly DependencyProperty PlayheadBrushProperty = DependencyProperty.Register(
        nameof(PlayheadBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FocusBrushProperty = DependencyProperty.Register(
        nameof(FocusBrush), typeof(Brush), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// True 表示波形仍在加载（读取 EGWV 缓存或首次分析），且当前没有可画的数据。
    /// 控件据此显示运动正弦波占位动画；第一个渐进快照到达后淡出、真实波形淡入。
    /// </summary>
    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(WaveformSeekBar),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsLoadingPropertyChanged));

    // 已播放区域的整体不透明度（owner 规范 25%–28%）：只压低存在感，不改变频段色相。
    private const double PlayedOpacity = 0.26;

    // 波形加载占位动画：延迟 120 ms 才出现（避免缓存命中时闪一下），淡入 160 ms；
    // 第一个真实快照到达后 220 ms 交叉淡化到真实波形。
    private const double PlaceholderDelaySeconds = 0.12;
    private const double PlaceholderFadeInSeconds = 0.16;
    private const double CrossfadeSeconds = 0.22;

    // 每个频段一条合并后的阶梯折线（StreamGeometry）；早期实现是 GeometryGroup +
    // 每设备像素列一个 RectangleGeometry，4K 下每次合成要遍历上万个 figure，帧率会掉到 10 fps
    // 量级（见 ADR-015 §9.2 的修订记录）。颜色仍由画刷在 OnRender 时给出，主题切换不重建几何。
    private StreamGeometry _lowGeometry = new();
    private StreamGeometry _midGeometry = new();
    private StreamGeometry _highGeometry = new();

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

    private readonly bool _animationsEnabled = SystemParameters.ClientAreaAnimation;
    private bool _animationHooked;
    private long _loadingStartedTimestamp;
    private long _transitionStartedTimestamp;

    public WaveformSeekBar()
    {
        Focusable = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        UseLayoutRounding = true;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
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

    /// <summary>True 表示波形数据仍在加载；为 true 且无快照时显示运动正弦波占位动画。</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

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

        var now = Stopwatch.GetTimestamp();
        if (peaks is null || peaks.ColumnCount <= 0)
        {
            var emptyPlaceholderOpacity = GetPlaceholderOpacity(now);
            if (emptyPlaceholderOpacity > 0.01)
            {
                dc.PushOpacity(emptyPlaceholderOpacity);
                DrawLoadingWave(dc, width, height);
                dc.Pop();
            }

            DrawFocusAdorner(dc, width, height);
            return;
        }

        EnsureGeometry(deviceWidth, deviceHeight, peaks);

        // 分段绘制：播放头右侧（未播放）100% 不透明，左侧（已播放）整体淡出到 26%。
        // 三频段仍是各自不透明的纯色填充，不做半透明混色（ADR-015）。
        var playheadDeviceX = Duration > 0 ? PlayheadDeviceX(deviceWidth) : 0;
        var playedWidth = Math.Clamp(playheadDeviceX, 0, deviceWidth);

        // 整层只画一遍：先按 100% 不透明度铺满三色带，再用轨道底色以 (1 - 26%) 覆盖已播放区。
        // 覆盖结果与「三色带在 26% 不透明度下叠在轨道底上」逐像素等价（轨道底为不透明纯色），
        // 但省掉一次整层几何绘制与两次 PushClip —— 4K 下这是 138 → 148 fps 的差别。
        DrawBand(dc, _lowGeometry, LowBrush);
        DrawBand(dc, _midGeometry, MidBrush);
        DrawBand(dc, _highGeometry, HighBrush);

        // 从占位动画切换到真实波形：波形逐渐显现，占位曲线同步淡出。
        var waveformOpacity = GetWaveformOpacity(now);
        if (waveformOpacity < 1)
        {
            // 波形本身已在上面按真实颜色绘制，这里用一层轨道底色按 (1 - 进度)
            // 覆盖，等价于淡入但不需要为整层几何重建透明度。
            if (TrackBrush is { } coverBrush)
            {
                dc.PushOpacity(1 - waveformOpacity);
                dc.DrawRoundedRectangle(coverBrush, null, bounds, 2, 2);
                dc.Pop();
            }
        }

        var placeholderOpacity = GetPlaceholderOpacity(now);
        if (placeholderOpacity > 0.01)
        {
            dc.PushOpacity(placeholderOpacity);
            DrawLoadingWave(dc, width, height);
            dc.Pop();
        }

        if (playedWidth > 0 && TrackBrush is { } playedOverlayBrush)
        {
            dc.PushOpacity(1 - PlayedOpacity);
            dc.DrawRectangle(playedOverlayBrush, null, DeviceRect(0, 0, playedWidth, deviceHeight));
            dc.Pop();
        }

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

    private void DrawBand(DrawingContext dc, Geometry geometry, Brush? brush)
    {
        if (brush is null || geometry.Bounds.IsEmpty)
        {
            return;
        }

        dc.DrawGeometry(brush, null, geometry);
    }

    /// <summary>
    /// 加载占位动画：三条频率 / 速度 / 振幅不同的正弦曲线（低 / 中 / 高频段色），
    /// 像一段正在跑动的波形。只在没有快照且 <see cref="IsLoading"/> 为 true 时绘制；
    /// 系统关闭动画时画静态相位，避免整条进度条空白。
    /// </summary>
    private void DrawLoadingWave(DrawingContext dc, double width, double height)
    {
        var phase = _animationsEnabled
            ? (Stopwatch.GetTimestamp() - _loadingStartedTimestamp) / (double)Stopwatch.Frequency
            : 0.6;

        var center = height * 0.54;
        DrawSine(dc, LowBrush, width, height, center, height * 0.26, 8.0, 4.6, 2.8, phase);
        DrawSine(dc, MidBrush, width, height, center, height * 0.16, 12.0, 5.4, 2.2, phase + 1.2);
        DrawSine(dc, HighBrush, width, height, center, height * 0.10, 18.0, 6.2, 1.6, phase + 2.4);
    }

    /// <summary>
    /// 画一条正弦“波形带”：用填充几何（上下两条正弦包成一条 ribbon）而不是描边。
    /// 描边几何在本控件（EdgeMode.Aliased 的波形控件）里存在不渲染的问题，
    /// 填充几何与真实波形同一条渲染路径，稳定可见。
    /// </summary>
    private static void DrawSine(
        DrawingContext dc,
        Brush? brush,
        double width,
        double height,
        double center,
        double amplitude,
        double turns,
        double speed,
        double thickness,
        double phase)
    {
        if (brush is null || width <= 0 || height <= 0)
        {
            return;
        }

        var step = Math.Max(2, width / 480);
        var half = thickness / 2;

        double Sample(double x)
        {
            var normalized = x / width;
            var y = center + amplitude * Math.Sin(2 * Math.PI * turns * normalized - speed * phase);
            return Math.Clamp(y, height * 0.06 + half, height * 0.94 - half);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, Sample(0) - half), true, true);
            for (var x = step; x <= width; x += step)
            {
                context.LineTo(new Point(x, Sample(x) - half), true, false);
            }

            for (var x = width; x >= 0; x -= step)
            {
                context.LineTo(new Point(x, Sample(x) + half), true, false);
            }
        }

        geometry.Freeze();
        dc.PushOpacity(0.6);
        dc.DrawGeometry(brush, null, geometry);
        dc.Pop();
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

    private static void OnPeaksPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (WaveformSeekBar)d;
        bar.InvalidateGeometry();
        bar.OnPeaksChanged((WaveformPeaks?)e.OldValue, (WaveformPeaks?)e.NewValue);
    }

    private void OnPeaksChanged(WaveformPeaks? oldPeaks, WaveformPeaks? newPeaks)
    {
        if (newPeaks is null)
        {
            _transitionStartedTimestamp = 0;
        }
        else if (oldPeaks is null)
        {
            // 第一个快照到达：占位动画淡出，真实波形淡入。渐进更新不再打断已开始的过渡。
            _transitionStartedTimestamp = _animationsEnabled ? Stopwatch.GetTimestamp() : 0;
        }

        UpdateAnimationHook();
        InvalidateVisual();
    }

    private static void OnIsLoadingPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (WaveformSeekBar)d;
        if ((bool)e.NewValue)
        {
            bar._loadingStartedTimestamp = Stopwatch.GetTimestamp();
            bar._transitionStartedTimestamp = 0;
        }

        bar.UpdateAnimationHook();
        bar.InvalidateVisual();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateAnimationHook();

    private void OnUnloaded(object sender, RoutedEventArgs e) => UnhookAnimation();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnAnimationFrame(object? sender, EventArgs e)
    {
        if (!_animationHooked)
        {
            return;
        }

        if (Peaks is null)
        {
            if (!IsLoading)
            {
                UnhookAnimation();
                return;
            }

            InvalidateVisual();
            return;
        }

        if (GetWaveformOpacity(Stopwatch.GetTimestamp()) >= 1)
        {
            UnhookAnimation();
            InvalidateVisual();
            return;
        }

        InvalidateVisual();
    }

    private void UpdateAnimationHook()
    {
        var needed = IsVisible && HasActiveAnimation();
        if (needed == _animationHooked)
        {
            return;
        }

        if (needed)
        {
            CompositionTarget.Rendering += OnAnimationFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnAnimationFrame;
        }

        _animationHooked = needed;
    }

    private void UnhookAnimation()
    {
        if (!_animationHooked)
        {
            return;
        }

        CompositionTarget.Rendering -= OnAnimationFrame;
        _animationHooked = false;
    }

    private bool HasActiveAnimation()
    {
        if (Peaks is null)
        {
            // 系统关闭客户端动画时占位波形保持静态，只需属性变化时重绘一次，不必逐帧挂 Rendering。
            return _animationsEnabled && IsLoading;
        }

        return _animationsEnabled && _transitionStartedTimestamp != 0 &&
               GetWaveformOpacity(Stopwatch.GetTimestamp()) < 1;
    }

    private double GetWaveformOpacity(long now)
    {
        if (Peaks is null)
        {
            return 0;
        }

        if (!_animationsEnabled || _transitionStartedTimestamp == 0)
        {
            return 1;
        }

        var seconds = (now - _transitionStartedTimestamp) / (double)Stopwatch.Frequency;
        return Math.Clamp(seconds / CrossfadeSeconds, 0, 1);
    }

    private double GetPlaceholderOpacity(long now)
    {
        if (Peaks is not null)
        {
            return 1 - GetWaveformOpacity(now);
        }

        if (!IsLoading)
        {
            return 0;
        }

        if (!_animationsEnabled)
        {
            return 1;
        }

        var seconds = (now - _loadingStartedTimestamp) / (double)Stopwatch.Frequency;
        if (seconds < PlaceholderDelaySeconds)
        {
            return 0;
        }

        return Math.Clamp((seconds - PlaceholderDelaySeconds) / PlaceholderFadeInSeconds, 0, 1) * 0.9;
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

        var validColumns = ClampValidCount(peaks);
        if (!windowChanged && validColumns <= _geometryValidCount)
        {
            return;
        }

        RebuildGeometry(deviceWidth, deviceHeight, peaks, validColumns);
    }

    /// <summary>
    /// 整层重建：把每个频段合成一条阶梯折线（一条 figure），而不是每列一个矩形 figure。
    /// StreamGeometry 只能 Open 一次，所以有效列变化时整层重建；波形分析期间约每秒数次。
    /// </summary>
    private void RebuildGeometry(int deviceWidth, int deviceHeight, WaveformPeaks peaks, int validColumns)
    {
        _geometryDeviceWidth = deviceWidth;
        _geometryDeviceHeight = deviceHeight;
        _geometryDpiScale = _dpiScale;
        _geometryColumnCount = peaks.ColumnCount;
        _geometryFloorDb = peaks.FloorDb;
        _geometryCeilingDb = peaks.CeilingDb;
        _geometryValidCount = validColumns;

        var columns = 0;
        if (validColumns > 0 && deviceWidth > 0)
        {
            for (var column = 0; column < deviceWidth; column++)
            {
                if ((long)column * peaks.ColumnCount / deviceWidth >= validColumns)
                {
                    break;
                }

                columns++;
            }
        }

        var low = new StreamGeometry();
        var mid = new StreamGeometry();
        var high = new StreamGeometry();
        if (columns > 0)
        {
            var lowBottom = new double[columns];
            var lowTop = new double[columns];
            var midBottom = new double[columns];
            var midTop = new double[columns];
            var highBottom = new double[columns];
            var highTop = new double[columns];

            for (var column = 0; column < columns; column++)
            {
                // 设备像素列 → 缓存列区间 [start, end)；缓存列比设备像素少时取最近的一列。
                var start = (int)((long)column * peaks.ColumnCount / deviceWidth);
                var end = (int)((long)(column + 1) * peaks.ColumnCount / deviceWidth);
                if (end <= start)
                {
                    end = start + 1;
                }

                end = Math.Min(end, validColumns);
                var length = end - start;

                var heightByte = length == 1 ? peaks.Height[start] : AggregateHeight(peaks.Height, start, length);
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

                var heightPx = heightByte == 0
                    ? 0
                    : Math.Clamp((int)Math.Round(heightByte / 255.0 * deviceHeight), 1, deviceHeight);
                var lowPx = Math.Clamp((int)Math.Round(heightPx * (lowByte / 255.0)), 0, heightPx);
                var midPx = Math.Clamp((int)Math.Round(heightPx * (midByte / 255.0)), 0, heightPx - lowPx);
                var highPx = heightPx - lowPx - midPx;

                lowBottom[column] = deviceHeight;
                lowTop[column] = deviceHeight - lowPx;
                midBottom[column] = lowTop[column];
                midTop[column] = lowTop[column] - midPx;
                highBottom[column] = midTop[column];
                highTop[column] = midTop[column] - highPx;
            }

            BuildBand(low, columns, lowBottom, lowTop, _dpiScale);
            BuildBand(mid, columns, midBottom, midTop, _dpiScale);
            BuildBand(high, columns, highBottom, highTop, _dpiScale);
        }

        low.Freeze();
        mid.Freeze();
        high.Freeze();
        _lowGeometry = low;
        _midGeometry = mid;
        _highGeometry = high;
    }

    /// <summary>
    /// 一条闭合阶梯折线：底边从左到右、顶边从右到左，中间用竖直台阶连接。
    /// 顶点数约为 4×列数，但只有一条 figure，软硬件合成都只需要处理一条路径。
    /// </summary>
    private static void BuildBand(
        StreamGeometry geometry, int columns, double[] bottomPx, double[] topPx, double dpiScale)
    {
        using var context = geometry.Open();
        context.BeginFigure(new Point(0, bottomPx[0] / dpiScale), true, true);

        for (var column = 0; column < columns; column++)
        {
            var right = (column + 1) / dpiScale;
            context.LineTo(new Point(right, bottomPx[column] / dpiScale), false, false);
            if (column + 1 < columns)
            {
                context.LineTo(new Point(right, bottomPx[column + 1] / dpiScale), false, false);
            }
        }

        for (var column = columns - 1; column >= 0; column--)
        {
            var left = column / dpiScale;
            var right = (column + 1) / dpiScale;
            context.LineTo(new Point(right, topPx[column] / dpiScale), false, false);
            context.LineTo(new Point(left, topPx[column] / dpiScale), false, false);
            if (column > 0)
            {
                context.LineTo(new Point(left, topPx[column - 1] / dpiScale), false, false);
            }
        }
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
}
