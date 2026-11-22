using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EchoGarage.App.Views.Controls;

/// <summary>
/// 「正在播放」页的背景柔光：两束由当前封面取色得到的径向渐变。
/// 颜色由视图模型计算后写入 <see cref="PrimaryColor"/> / <see cref="SecondaryColor"/>。
///
/// 实现方式：控件是一个 <see cref="Grid"/>，内部两层 <see cref="CoverGlowLayer"/> 各自保存一份
/// 已冻结的渐变绘制。换歌时旧层淡出、新层淡入（只动画两层的 Opacity），因此：
/// <list type="bullet">
/// <item>过渡是真正的交叉淡化，不会像“每次从全透明淡入”那样让旧柔光瞬间消失；</item>
/// <item>动画期间不重绘渐变本身（每层内容只构造一次），不会每帧重新光栅化大面积渐变；</item>
/// <item>空闲时不占 CPU（不用 CompositionTarget.Rendering）。</item>
/// </list>
/// </summary>
public sealed class CoverGlow : Grid
{
    private const double FadeMilliseconds = 450;

    /// <summary>第一束光的颜色（来自封面主色，或主题强调色回退）。</summary>
    public static readonly DependencyProperty PrimaryColorProperty = DependencyProperty.Register(
        nameof(PrimaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, OnGlowPropertyChanged));

    /// <summary>第二束光的颜色（来自封面次色）。</summary>
    public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(
        nameof(SecondaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, OnGlowPropertyChanged));

    /// <summary>第一束光的峰值不透明度，按主题资源 NowPlayingGlowPrimaryAlpha 绑定。</summary>
    public static readonly DependencyProperty PrimaryOpacityProperty = DependencyProperty.Register(
        nameof(PrimaryOpacity), typeof(double), typeof(CoverGlow),
        new FrameworkPropertyMetadata(0.25, OnGlowPropertyChanged));

    /// <summary>第二束光的峰值不透明度，按主题资源 NowPlayingGlowSecondaryAlpha 绑定。</summary>
    public static readonly DependencyProperty SecondaryOpacityProperty = DependencyProperty.Register(
        nameof(SecondaryOpacity), typeof(double), typeof(CoverGlow),
        new FrameworkPropertyMetadata(0.13, OnGlowPropertyChanged));

    private readonly CoverGlowLayer _layerA = new();
    private readonly CoverGlowLayer _layerB = new();
    private CoverGlowLayer _activeLayer;
    private CoverGlowLayer _inactiveLayer;
    private int _transitionVersion;
    private bool _hasPaintedGlow;

    public CoverGlow()
    {
        IsHitTestVisible = false;
        ClipToBounds = false;
        SnapsToDevicePixels = false;

        foreach (var layer in new[] { _layerA, _layerB })
        {
            layer.HorizontalAlignment = HorizontalAlignment.Stretch;
            layer.VerticalAlignment = VerticalAlignment.Stretch;
            layer.Opacity = 0;
            Children.Add(layer);
        }

        _activeLayer = _layerA;
        _inactiveLayer = _layerB;
    }

    public Color PrimaryColor
    {
        get => (Color)GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    public Color SecondaryColor
    {
        get => (Color)GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    public double PrimaryOpacity
    {
        get => (double)GetValue(PrimaryOpacityProperty);
        set => SetValue(PrimaryOpacityProperty, value);
    }

    public double SecondaryOpacity
    {
        get => (double)GetValue(SecondaryOpacityProperty);
        set => SetValue(SecondaryOpacityProperty, value);
    }

    private static void OnGlowPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CoverGlow)d).ApplyGlow(animate: true);

    private void ApplyGlow(bool animate)
    {
        var incoming = _inactiveLayer;
        incoming.SetGlow(
            PrimaryColor,
            Math.Clamp(PrimaryOpacity, 0.0, 1.0) * (PrimaryColor.A / 255.0),
            SecondaryColor,
            Math.Clamp(SecondaryOpacity, 0.0, 1.0) * (SecondaryColor.A / 255.0));

        var version = ++_transitionVersion;

        // 首次赋值（页面刚加载 / 刚切到本页）直接显示，避免一进来就淡入一次。
        if (!animate || !_hasPaintedGlow || !SystemParameters.ClientAreaAnimation)
        {
            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = 1;
            incoming.Visibility = Visibility.Visible;

            _activeLayer.BeginAnimation(OpacityProperty, null);
            _activeLayer.Opacity = 0;
            _activeLayer.Visibility = Visibility.Collapsed;

            SwapLayers();
            _hasPaintedGlow = true;
            return;
        }

        var outgoing = _activeLayer;
        var duration = TimeSpan.FromMilliseconds(FadeMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // 新层先以 base=1 出现，并让动画从 0 淡入；结束后清掉动画才会落到 base=1。
        incoming.BeginAnimation(OpacityProperty, null);
        incoming.Opacity = 1;
        incoming.Visibility = Visibility.Visible;

        // 旧层从当前可见不透明度淡出（快速连切时能接住被中断的动画进度）。
        var outgoingStart = outgoing.Opacity;
        outgoing.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0.0, 1.0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };
        var fadeOut = new DoubleAnimation(outgoingStart, 0.0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };

        fadeIn.Completed += (_, _) =>
        {
            if (version != _transitionVersion)
            {
                return;
            }

            incoming.BeginAnimation(OpacityProperty, null);
            incoming.Opacity = 1;
            _hasPaintedGlow = true;
        };
        fadeOut.Completed += (_, _) =>
        {
            if (version != _transitionVersion)
            {
                return;
            }

            outgoing.BeginAnimation(OpacityProperty, null);
            outgoing.Opacity = 0;
            outgoing.Visibility = Visibility.Collapsed;
        };

        incoming.BeginAnimation(OpacityProperty, fadeIn);
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        SwapLayers();
        _hasPaintedGlow = true;
    }

    private void SwapLayers()
    {
        (_activeLayer, _inactiveLayer) = (_inactiveLayer, _activeLayer);
    }

    /// <summary>
    /// 单个柔光图层：把两束径向渐变画进自己的绘制内容。内容只在 <see cref="SetGlow"/> 时重建，
    /// 交叉淡化期间只改 Opacity，不触发重新光栅化。
    /// </summary>
    private sealed class CoverGlowLayer : FrameworkElement
    {
        private Brush? _primaryBrush;
        private Brush? _secondaryBrush;

        public void SetGlow(Color primary, double primaryPeak, Color secondary, double secondaryPeak)
        {
            _primaryBrush = CreateGlowBrush(primary, primaryPeak, 0.16, 0.22);
            _secondaryBrush = CreateGlowBrush(secondary, secondaryPeak, 0.48, 0.14);
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var bounds = new Rect(0, 0, width, height);
            if (_primaryBrush is not null)
            {
                drawingContext.DrawRectangle(_primaryBrush, null, bounds);
            }

            if (_secondaryBrush is not null)
            {
                drawingContext.DrawRectangle(_secondaryBrush, null, bounds);
            }
        }

        private static Brush? CreateGlowBrush(Color color, double peakOpacity, double centerX, double centerY)
        {
            var peak = Math.Clamp(peakOpacity, 0.0, 1.0) * (color.A / 255.0);
            if (peak <= 0.001)
            {
                return null;
            }

            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                Center = new Point(centerX, centerY),
                GradientOrigin = new Point(centerX, centerY),
                RadiusX = 0.55,
                RadiusY = 0.55,
            };

            // a(r) = peak * (1 - r)^2，取 5 个 stop。末尾 stop 必须保持相同 RGB + 全透明，
            // 不能直接用 Colors.Transparent（WPF 在 sRGB 空间插值，透明黑会画出灰黑色晕边）。
            double[] positions = [0.0, 0.25, 0.5, 0.75, 1.0];
            foreach (var position in positions)
            {
                var factor = (1.0 - position) * (1.0 - position);
                var alpha = (byte)Math.Clamp((int)Math.Round(peak * factor * 255.0), 0, 255);
                brush.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, color.R, color.G, color.B), position));
            }

            brush.Freeze();
            return brush;
        }
    }
}
