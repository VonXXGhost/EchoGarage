using System.Globalization;
using System.Windows;
using System.Windows.Media;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views.Controls;

/// <summary>
/// 右栏「播放可视化」的分段频谱图（ADR-023 §5–§9）。
///
/// 渲染模型是「拉取式」：挂上 <see cref="CompositionTarget.Rendering"/> 后每帧向
/// <see cref="ISpectrumSource"/> 要一帧 <see cref="SpectrumFrame"/>，再把柱高画出来。
/// 窗口被遮挡 / 最小化时 WPF 不再出渲染帧，分析也随之停摆，不会空转 CPU。
///
/// 颜色一律来自主题令牌（<c>Resources/Themes/*.xaml</c>，经 Controls.xaml 的样式绑定到依赖属性），
/// 控件本身不含任何字面颜色。
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    private static readonly Typeface LabelTypeface = new(
        SystemFonts.MessageFontFamily,
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>柱体颜色渐变的下端（面板底部）。</summary>
    public static readonly DependencyProperty BarBottomBrushProperty = DependencyProperty.Register(
        nameof(BarBottomBrush),
        typeof(Brush),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    /// <summary>柱体颜色渐变的上端（最高柱的顶端）。</summary>
    public static readonly DependencyProperty BarTopBrushProperty = DependencyProperty.Register(
        nameof(BarTopBrush),
        typeof(Brush),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    /// <summary>峰值保持线的颜色。</summary>
    public static readonly DependencyProperty PeakBrushProperty = DependencyProperty.Register(
        nameof(PeakBrush),
        typeof(Brush),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    /// <summary>dB 刻度线的颜色。</summary>
    public static readonly DependencyProperty GraticuleBrushProperty = DependencyProperty.Register(
        nameof(GraticuleBrush),
        typeof(Brush),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    /// <summary>刻度文字的颜色。</summary>
    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush),
        typeof(Brush),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    /// <summary>采样数据源；为空时只画刻度。</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ISpectrumSource),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    /// <summary>渲染参数快照（由视图模型在设置变更时整体替换）。</summary>
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options),
        typeof(SpectrumOptions),
        typeof(SpectrumView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStaticLayerInvalidated));

    private const double MinimumWidthForDbLabels = 200;

    private const double MinimumWidthForFrequencyLabels = 220;

    private const double DbLabelColumnWidth = 26;

    private const double FrequencyLabelHeight = 14;

    /// <summary>相邻频率标签之间至少留出的空隙（DIP）。</summary>
    private const double LabelGap = 5;

    /// <summary>绘图区顶部留白：0 dB 刻度线与峰值线不贴边。</summary>
    private const double PlotTopInset = 3;

    private SpectrumFrame? _frame;
    private TimeSpan _lastRenderingTime;
    private bool _hooked;
    private double _dpi = 1;

    private double _staticWidth;
    private double _staticHeight;
    private double _staticDpi;
    private SpectrumOptions? _staticOptions;
    private Brush? _staticGridBrush;
    private Brush? _staticLabelBrush;
    private Brush? _staticBarBottomBrush;
    private Brush? _staticBarTopBrush;
    private bool _staticValid;

    private Rect _plot;
    private Geometry? _gridGeometry;
    private Pen? _gridPen;
    private LinearGradientBrush? _barFill;
    private List<(FormattedText Text, Point Origin)> _labels = [];

    private long _renderedFrames;
    private double _renderTotalMs;
    private double _renderMaxMs;

    private long _renderingTicks;
    private TimeSpan _firstRenderingTime;

    public SpectrumView()
    {
        Focusable = false;
        IsHitTestVisible = false;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public Brush? BarBottomBrush
    {
        get => (Brush?)GetValue(BarBottomBrushProperty);
        set => SetValue(BarBottomBrushProperty, value);
    }

    public Brush? BarTopBrush
    {
        get => (Brush?)GetValue(BarTopBrushProperty);
        set => SetValue(BarTopBrushProperty, value);
    }

    public Brush? PeakBrush
    {
        get => (Brush?)GetValue(PeakBrushProperty);
        set => SetValue(PeakBrushProperty, value);
    }

    public Brush? GraticuleBrush
    {
        get => (Brush?)GetValue(GraticuleBrushProperty);
        set => SetValue(GraticuleBrushProperty, value);
    }

    public Brush? LabelBrush
    {
        get => (Brush?)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public ISpectrumSource? Source
    {
        get => (ISpectrumSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public SpectrumOptions? Options
    {
        get => (SpectrumOptions?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    /// <summary>已绘制的帧数（诊断用：配合经过时间算实际 FPS）。</summary>
    public long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    /// <summary>收到的 <see cref="CompositionTarget.Rendering"/> 帧数（诊断用，含尚未绘制的帧）。</summary>
    public long RenderingTicks => Interlocked.Read(ref _renderingTicks);

    /// <summary>统计窗口内的秒数（首帧到最近一帧的 <c>RenderingTime</c> 之差，诊断用）。</summary>
    public double MeasuredSeconds
    {
        get
        {
            var first = _firstRenderingTime;
            var last = _lastRenderingTime;
            return first == TimeSpan.Zero || last <= first ? 0 : (last - first).TotalSeconds;
        }
    }

    /// <summary>WPF 渲染 tick 的帧率（诊断用：判断是「渲染被拖慢」还是「事件本身变少」）。</summary>
    public double RenderingFramesPerSecond
    {
        get
        {
            var seconds = MeasuredSeconds;
            return seconds > 0 ? Interlocked.Read(ref _renderingTicks) / seconds : 0;
        }
    }

    /// <summary>实际完成绘制的帧率（诊断用）。</summary>
    public double RenderedFramesPerSecond
    {
        get
        {
            var seconds = MeasuredSeconds;
            return seconds > 0 ? Interlocked.Read(ref _renderedFrames) / seconds : 0;
        }
    }

    /// <summary>每帧 OnRender 的平均耗时（毫秒，诊断用）。</summary>
    public double AverageRenderMilliseconds => _renderedFrames == 0 ? 0 : _renderTotalMs / _renderedFrames;

    /// <summary>单帧 OnRender 的最大耗时（毫秒，诊断用）。</summary>
    public double MaxRenderMilliseconds => _renderMaxMs;

    /// <summary>当前帧（诊断用）。</summary>
    public SpectrumFrame? CurrentFrame => _frame;

    /// <summary>清零渲染统计，便于从某一时刻开始测量帧率与每帧耗时。</summary>
    public void ResetRenderStatistics()
    {
        Interlocked.Exchange(ref _renderedFrames, 0);
        Interlocked.Exchange(ref _renderingTicks, 0);
        _renderTotalMs = 0;
        _renderMaxMs = 0;
        _firstRenderingTime = TimeSpan.Zero;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsInfinity(availableSize.Height) ? 120 : availableSize.Height;
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateStaticLayer();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX <= 0 ? 1 : newDpi.DpiScaleX;
        InvalidateStaticLayer();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureStaticLayer(width, height);

        if (_gridPen is { } gridPen && _gridGeometry is not null)
        {
            dc.DrawGeometry(null, gridPen, _gridGeometry);
        }

        foreach (var (text, origin) in _labels)
        {
            dc.DrawText(text, origin);
        }

        var frame = _frame;
        if (frame is null || frame.BandCount == 0 || _plot.Width <= 0 || _plot.Height <= 0)
        {
            RecordRender(started);
            return;
        }

        DrawBars(dc, frame);
        DrawPeaks(dc, frame);
        RecordRender(started);
    }

    private void DrawBars(DrawingContext dc, SpectrumFrame frame)
    {
        if (BarBottomBrush is null || BarTopBrush is null)
        {
            return;
        }

        var levels = frame.Levels;
        var bandCount = levels.Length;
        var bandWidth = _plot.Width / bandCount;
        if (bandWidth <= 0)
        {
            return;
        }

        // 间隙：窄面板上不留缝（否则柱体瘦得看不见），宽了才留 18% 的呼吸空间。
        var gap = ComputeBandGap(bandWidth);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < bandCount; i++)
            {
                var level = levels[i];
                if (!double.IsFinite(level) || level <= 0)
                {
                    continue;
                }

                var barHeight = Math.Min(1.0, level) * _plot.Height;
                if (barHeight < 0.75)
                {
                    // 小于一个设备像素的柱体不画：省掉无意义的几何，也避免糊成一条灰线。
                    continue;
                }

                var left = Snap(_plot.Left + i * bandWidth + gap / 2);
                var right = Snap(_plot.Left + (i + 1) * bandWidth - gap / 2);
                if (right - left < 1 / _dpi)
                {
                    right = left + 1 / _dpi;
                }

                var top = Snap(_plot.Bottom - barHeight);
                var bottom = Snap(_plot.Bottom);

                context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
                context.LineTo(new Point(right, top), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(right, bottom), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(left, bottom), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();

        // 竖直渐变按「整个绘图区」映射，而不是按柱体几何的包围盒：
        // 短柱只露出渐变下端的深色，高柱才能一路爬到亮色——与 foobar2000 的观感一致。
        // 做法是用绘图区矩形铺渐变，再把绘制裁剪到柱体几何上（一次 DrawRectangle，开销可忽略）。
        // 渐变刷由 EnsureStaticLayer 按主题令牌缓存，这里只做一次绘制。
        if (_barFill is null)
        {
            return;
        }

        dc.PushClip(geometry);
        dc.DrawRectangle(_barFill, null, _plot);
        dc.Pop();
    }

    /// <summary>柱体间隙：按设备像素对齐；窄柱（频段很多或面板很窄）不留缝。</summary>
    private double ComputeBandGap(double bandWidth)
    {
        if (bandWidth < 6)
        {
            return 0;
        }

        var gap = Math.Min(2.0, bandWidth * 0.18);
        return Math.Round(gap * _dpi) / _dpi;
    }

    private void DrawPeaks(DrawingContext dc, SpectrumFrame frame)
    {
        if (PeakBrush is null || _frame is null)
        {
            return;
        }

        var options = Options ?? SpectrumOptions.Default;
        if (!options.ShowPeaks)
        {
            return;
        }

        var levels = frame.Levels;
        var peaks = frame.Peaks;
        var bandCount = levels.Length;
        var bandWidth = _plot.Width / bandCount;
        var gap = ComputeBandGap(bandWidth);
        var height = 1 / _dpi;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            for (var i = 0; i < bandCount && i < peaks.Length; i++)
            {
                var peak = peaks[i];
                if (!double.IsFinite(peak) || peak <= 0)
                {
                    continue;
                }

                var left = Snap(_plot.Left + i * bandWidth + gap / 2);
                var right = Snap(_plot.Left + (i + 1) * bandWidth - gap / 2);
                if (right - left < 1 / _dpi)
                {
                    right = left + 1 / _dpi;
                }

                var top = Snap(_plot.Bottom - Math.Min(1.0, peak) * _plot.Height);
                context.BeginFigure(new Point(left, top), isFilled: true, isClosed: true);
                context.LineTo(new Point(right, top), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(right, top + height), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(left, top + height), isStroked: false, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(PeakBrush, null, geometry);
    }

    private void EnsureStaticLayer(double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (dpi <= 0)
        {
            dpi = 1;
        }

        _dpi = dpi;

        var options = Options ?? SpectrumOptions.Default;
        if (_staticValid
            && Math.Abs(_staticWidth - width) < 0.01
            && Math.Abs(_staticHeight - height) < 0.01
            && Math.Abs(_staticDpi - dpi) < 0.001
            && ReferenceEquals(_staticOptions, options)
            && ReferenceEquals(_staticGridBrush, GraticuleBrush)
            && ReferenceEquals(_staticLabelBrush, LabelBrush)
            && ReferenceEquals(_staticBarBottomBrush, BarBottomBrush)
            && ReferenceEquals(_staticBarTopBrush, BarTopBrush))
        {
            return;
        }

        _staticWidth = width;
        _staticHeight = height;
        _staticDpi = dpi;
        _staticOptions = options;
        _staticGridBrush = GraticuleBrush;
        _staticLabelBrush = LabelBrush;
        _staticBarBottomBrush = BarBottomBrush;
        _staticBarTopBrush = BarTopBrush;
        _staticValid = true;

        // 画笔与渐变刷只跟主题令牌 / DPI 有关，缓存起来，避免每帧 new。
        if (GraticuleBrush is { } gridBrush)
        {
            var pen = new Pen(gridBrush, 1 / Math.Max(_dpi, 0.01));
            pen.Freeze();
            _gridPen = pen;
        }
        else
        {
            _gridPen = null;
        }

        if (BarBottomBrush is not null && BarTopBrush is not null)
        {
            var fill = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                StartPoint = new Point(0, 1),
                EndPoint = new Point(0, 0),
            };
            fill.GradientStops.Add(new GradientStop(GetBrushColor(BarBottomBrush, Colors.Transparent), 0));
            fill.GradientStops.Add(new GradientStop(GetBrushColor(BarTopBrush, Colors.Transparent), 1));
            fill.Freeze();
            _barFill = fill;
        }
        else
        {
            _barFill = null;
        }

        var showDbLabels = options.ShowGraticule && width >= MinimumWidthForDbLabels;
        var showFrequencyLabels = options.ShowFrequencyLabels && width >= MinimumWidthForFrequencyLabels;
        var dbColumn = showDbLabels ? DbLabelColumnWidth : 0;
        var labelRow = showFrequencyLabels ? FrequencyLabelHeight : 0;
        _plot = new Rect(0, PlotTopInset, Math.Max(0, width - dbColumn),
            Math.Max(1, height - labelRow - PlotTopInset));

        var labels = new List<(FormattedText, Point)>();
        var lines = new StreamGeometry();
        using (var context = lines.Open())
        {
            if (options.ShowGraticule && _plot.Height > 8 && GraticuleBrush is not null)
            {
                var floorDb = options.FloorDb;
                var ceilingDb = options.CeilingDb;
                for (var db = ceilingDb; db >= floorDb - 0.5; db -= 10)
                {
                    var y = Snap(_plot.Bottom - SpectrumMath.Normalize(db, floorDb, ceilingDb) * _plot.Height);
                    ctxLine(context, y);
                }

                if (showDbLabels && LabelBrush is not null)
                {
                    for (var db = ceilingDb; db >= floorDb - 0.5; db -= 10)
                    {
                        var y = Snap(_plot.Bottom - SpectrumMath.Normalize(db, floorDb, ceilingDb) * _plot.Height);
                        var text = FormatDbLabel(db);
                        var formatted = CreateLabel(text);
                        // 0 dB 标签的顶部会超出绘图区，夹紧到控件内，避免被 ClipToBounds 切掉。
                        var labelY = Math.Clamp(y - formatted.Height / 2, 0,
                            Math.Max(0, _plot.Bottom - formatted.Height));
                        labels.Add((formatted, new Point(_plot.Right + 3, labelY)));
                    }
                }
            }
        }

        _gridGeometry = lines;
        _gridGeometry.Freeze();

        if (showFrequencyLabels && LabelBrush is not null && options.BandEdges.Count > 1)
        {
            // 与 foobar2000 一致：每个标签左对齐到自己那根柱体的左边界（所在频段的下限）。
            // 窄面板放不下全部刻度，就按实际字宽从低频往高频贪心放置，只丢掉中间的部分刻度。
            var bandWidth = _plot.Width / options.BandCount;
            var baselineY = height - labelRow + 1;
            var lastRight = double.NegativeInfinity;
            for (var i = 0; i < options.BandCount && i < options.BandEdges.Count; i++)
            {
                var formatted = CreateLabel(SpectrumMath.FormatFrequencyLabel(options.BandEdges[i]));
                var x = _plot.Left + i * bandWidth;
                if (x + formatted.Width > width)
                {
                    x = Math.Max(0, width - formatted.Width);
                }

                if (x < lastRight + LabelGap)
                {
                    continue;
                }

                labels.Add((formatted, new Point(x, baselineY)));
                lastRight = x + formatted.Width;
            }
        }

        _labels = labels;

        void ctxLine(StreamGeometryContext context, double y)
        {
            context.BeginFigure(new Point(_plot.Left, y), isFilled: false, isClosed: false);
            context.LineTo(new Point(_plot.Right, y), isStroked: true, isSmoothJoin: false);
        }
    }

    private string FormatDbLabel(double db)
    {
        var value = (int)Math.Round(db);
        return value == 0 ? "0 dB" : value.ToString(CultureInfo.InvariantCulture);
    }

    private FormattedText CreateLabel(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        LabelTypeface,
        9,
        LabelBrush ?? Brushes.Transparent,
        _dpi);

    private double Snap(double value) => Math.Round(value * _dpi) / _dpi;

    private static Color GetBrushColor(Brush? brush, Color fallback) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        _ => fallback,
    };

    private void InvalidateStaticLayer()
    {
        _staticValid = false;
        InvalidateVisual();
    }

    private void RecordRender(long started)
    {
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Increment(ref _renderedFrames);
        _renderTotalMs += elapsed;
        if (elapsed > _renderMaxMs)
        {
            _renderMaxMs = elapsed;
        }
    }

    private static void OnStaticLayerInvalidated(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpectrumView view)
        {
            view._frame = null;
            view.InvalidateStaticLayer();
        }
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SpectrumView view)
        {
            return;
        }

        view._lastRenderingTime = TimeSpan.Zero;
        view._firstRenderingTime = TimeSpan.Zero;
        view._frame = null;
        view.InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || Source is not { } source)
        {
            return;
        }

        if (e is not RenderingEventArgs rendering)
        {
            return;
        }

        if (rendering.RenderingTime == _lastRenderingTime)
        {
            // 同一帧里 WPF 可能触发多次 Rendering：只分析 / 重绘一次，否则弹道速度会被放大。
            return;
        }

        Interlocked.Increment(ref _renderingTicks);
        if (_firstRenderingTime == TimeSpan.Zero)
        {
            _firstRenderingTime = rendering.RenderingTime;
        }

        var dt = _lastRenderingTime == TimeSpan.Zero
            ? 1.0 / 60.0
            : (rendering.RenderingTime - _lastRenderingTime).TotalSeconds;
        _lastRenderingTime = rendering.RenderingTime;

        _frame = source.NextFrame(dt);
        InvalidateVisual();
    }

    private void Hook()
    {
        if (_hooked)
        {
            return;
        }

        _hooked = true;
        _lastRenderingTime = TimeSpan.Zero;
        _firstRenderingTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Unhook()
    {
        if (!_hooked)
        {
            return;
        }

        _hooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lastRenderingTime = TimeSpan.Zero;
        Hook();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Unhook();
}
