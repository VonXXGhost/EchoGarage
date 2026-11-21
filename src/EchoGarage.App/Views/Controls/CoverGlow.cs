using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EchoGarage.App.Views.Controls;

/// <summary>
/// 「正在播放」页的背景柔光：两束由当前封面取色得到的径向渐变。
/// 颜色由视图模型计算后写入 <see cref="PrimaryColor"/> / <see cref="SecondaryColor"/>；
/// 控件只负责按主题给定的峰值透明度绘制，并在颜色变化时做 450ms 交叉淡入。
/// 仅在颜色 / 透明度 / 尺寸变化时重绘，空闲时不占 CPU（不用 CompositionTarget.Rendering）。
/// </summary>
public sealed class CoverGlow : FrameworkElement
{
    private const double FadeMilliseconds = 450;

    // 动画目标必须是可写的依赖属性（只读 DP 的元数据带 IsAnimationProhibited），
    // 因此这里用普通 Register，只对外暴露只读的 CLR 属性。
    public static readonly DependencyProperty AnimatedPrimaryColorProperty = DependencyProperty.Register(
        nameof(AnimatedPrimaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimatedSecondaryColorProperty = DependencyProperty.Register(
        nameof(AnimatedSecondaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>第一束光的颜色（来自封面主色，或主题强调色回退）。</summary>
    public static readonly DependencyProperty PrimaryColorProperty = DependencyProperty.Register(
        nameof(PrimaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, OnPrimaryColorChanged));

    /// <summary>第二束光的颜色（来自封面次色）。</summary>
    public static readonly DependencyProperty SecondaryColorProperty = DependencyProperty.Register(
        nameof(SecondaryColor), typeof(Color), typeof(CoverGlow),
        new FrameworkPropertyMetadata(Colors.Transparent, OnSecondaryColorChanged));

    /// <summary>第一束光的峰值不透明度，按主题资源 NowPlayingGlowPrimaryAlpha 绑定。</summary>
    public static readonly DependencyProperty PrimaryOpacityProperty = DependencyProperty.Register(
        nameof(PrimaryOpacity), typeof(double), typeof(CoverGlow),
        new FrameworkPropertyMetadata(0.25, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>第二束光的峰值不透明度，按主题资源 NowPlayingGlowSecondaryAlpha 绑定。</summary>
    public static readonly DependencyProperty SecondaryOpacityProperty = DependencyProperty.Register(
        nameof(SecondaryOpacity), typeof(double), typeof(CoverGlow),
        new FrameworkPropertyMetadata(0.13, FrameworkPropertyMetadataOptions.AffectsRender));

    public CoverGlow()
    {
        IsHitTestVisible = false;
    }

    public Color AnimatedPrimaryColor => (Color)GetValue(AnimatedPrimaryColorProperty);

    public Color AnimatedSecondaryColor => (Color)GetValue(AnimatedSecondaryColorProperty);

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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        DrawGlow(drawingContext, AnimatedPrimaryColor, PrimaryOpacity, 0.16, 0.22, 0.55, width, height);
        DrawGlow(drawingContext, AnimatedSecondaryColor, SecondaryOpacity, 0.48, 0.14, 0.55, width, height);
    }

    private static void DrawGlow(DrawingContext drawingContext, Color color, double peakOpacity,
        double centerX, double centerY, double radius, double width, double height)
    {
        var peak = Math.Clamp(peakOpacity, 0.0, 1.0) * (color.A / 255.0);
        if (peak <= 0.001)
        {
            return;
        }

        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(centerX, centerY),
            GradientOrigin = new Point(centerX, centerY),
            RadiusX = radius,
            RadiusY = radius,
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
        drawingContext.DrawRectangle(brush, null, new Rect(0, 0, width, height));
    }

    private static void OnPrimaryColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CoverGlow)d).AnimateTo(AnimatedPrimaryColorProperty, (Color)e.NewValue);

    private static void OnSecondaryColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CoverGlow)d).AnimateTo(AnimatedSecondaryColorProperty, (Color)e.NewValue);

    private void AnimateTo(DependencyProperty animatedProperty, Color target)
    {
        var from = (Color)GetValue(animatedProperty);
        BeginAnimation(animatedProperty, null);

        if (!SystemParameters.ClientAreaAnimation || (from.A == 0 && target.A == 0))
        {
            SetValue(animatedProperty, target);
            return;
        }

        BeginAnimation(animatedProperty, new ColorAnimation(from, target, TimeSpan.FromMilliseconds(FadeMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
