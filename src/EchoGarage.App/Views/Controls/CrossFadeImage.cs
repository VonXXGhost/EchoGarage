using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EchoGarage.App.Views.Controls;

/// <summary>
/// 封面交叉淡入控件（右栏「正在播放」大封面用）。
///
/// 内部两层 Image：上层显示当前封面，下层保留上一张封面。Source 变化时旧图先留在下层，
/// 新图在上层用很短的透明度淡入（默认 200ms），因此切歌既不会出现旧图瞬间消失的硬切，
/// 也不会经过空白背景或闪烁的转圈。新曲目没有封面（Source 变为 null）时当前封面淡出，
/// 由父容器里位于本控件下方的占位符接手显示。
///
/// 动画被打断（快速连续切歌）时先快进到上一次过渡的终点，再开始新的交叉淡入，避免半透明叠影。
/// 时长固定为短促的 200ms，owner 要求“平滑但不拖沓”；EaseOut 让主体变化集中在开头，观感更利落。
/// </summary>
public sealed class CrossFadeImage : Grid
{
    /// <summary>默认过渡时长。设为 0 可关闭动画（诊断或低性能场景）。</summary>
    public static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>防御性上限：XAML 里误配超长时长时也不会让封面切换拖沓。</summary>
    private static readonly TimeSpan MaxTransitionDuration = TimeSpan.FromSeconds(2);

    public static readonly DependencyProperty TransitionDurationProperty = DependencyProperty.Register(
        nameof(TransitionDuration), typeof(TimeSpan), typeof(CrossFadeImage),
        new FrameworkPropertyMetadata(DefaultTransitionDuration));

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(CrossFadeImage),
        new FrameworkPropertyMetadata(null, OnSourceChanged));

    private readonly Image _previousImage;
    private readonly Image _currentImage;
    private bool _isTransitioning;
    private int _transitionVersion;

    public CrossFadeImage()
    {
        _previousImage = CreateLayer();
        _currentImage = CreateLayer();
        // 先加下层旧图，再加上层当前图：Grid 里靠后的子元素绘制在上层。
        Children.Add(_previousImage);
        Children.Add(_currentImage);
    }

    /// <summary>当前显示的封面。赋值后旧图会作为下层保留，新图在上层交叉淡入。</summary>
    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>交叉淡入时长；小于等于 0 时立即切换。</summary>
    public TimeSpan TransitionDuration
    {
        get => (TimeSpan)GetValue(TransitionDurationProperty);
        set => SetValue(TransitionDurationProperty, value);
    }

    private static Image CreateLayer()
    {
        // 与原来的单张封面 Image 保持一致：Uniform + 高质量缩放（420px 解码到约 300px 显示）。
        var layer = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.HighQuality);
        return layer;
    }

    private static void OnSourceChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e) =>
        ((CrossFadeImage)dependencyObject).ApplySource((ImageSource?)e.NewValue);

    private void ApplySource(ImageSource? source)
    {
        // 上一次过渡可能还没结束（快速连续切歌）：先快进到终点（当前图完全不透明），
        // 否则新的淡入会从上一次的半透明中间态开始，旧图透出来形成叠影。
        if (_isTransitioning)
        {
            FinishTransition();
        }

        var previous = _currentImage.Source;
        if (ReferenceEquals(previous, source))
        {
            return;
        }

        if (source is null)
        {
            FadeOutCurrent();
            return;
        }

        // 旧图压到下层，新图从透明淡入：过渡全程都有画面，不会闪出占位符或空白。
        _previousImage.Source = previous;
        _currentImage.Source = source;
        FadeInCurrent();
    }

    private void FadeInCurrent()
    {
        var duration = EffectiveDuration;
        if (duration == TimeSpan.Zero)
        {
            _currentImage.Opacity = 1;
            _previousImage.Source = null;
            return;
        }

        AnimateCurrentOpacity(from: 0d, to: 1d, duration, onCompleted: () =>
        {
            // 交叉淡入结束后新图已完全不透明，下层的旧图不可见，可以释放。
            _previousImage.Source = null;
        });
    }

    private void FadeOutCurrent()
    {
        var duration = EffectiveDuration;
        if (duration == TimeSpan.Zero)
        {
            _currentImage.Source = null;
            _previousImage.Source = null;
            return;
        }

        // 淡出期间下层不显示任何图：封面框背景逐渐透出，父容器里的占位符随之显现。
        _previousImage.Source = null;
        AnimateCurrentOpacity(from: 1d, to: 0d, duration, onCompleted: () =>
        {
            // 动画结束后再清空 Source，避免淡出中途硬切到占位符。
            _currentImage.Source = null;
        });
    }

    /// <summary>
    /// 对当前层做一次透明度动画。HoldEnd 保证动画结束前合成值稳定；结束回调里移除动画、
    /// 让基础值（恒为 1）接管，并通过 version 守卫保证只有最新一次过渡会修改层状态。
    /// </summary>
    private void AnimateCurrentOpacity(double from, double to, TimeSpan duration, Action onCompleted)
    {
        var version = ++_transitionVersion;
        _isTransitioning = true;
        // 基础值保持 1：动画被移除后当前层不会停在 HoldEnd 的 0（否则下一次淡入会闪一下）。
        _currentImage.Opacity = 1;
        var animation = new DoubleAnimation(from, to, new Duration(duration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) =>
        {
            if (version != _transitionVersion)
            {
                return;
            }

            _currentImage.BeginAnimation(OpacityProperty, null);
            _isTransitioning = false;
            onCompleted();
        };
        _currentImage.BeginAnimation(OpacityProperty, animation);
    }

    private void FinishTransition()
    {
        _transitionVersion++;
        _currentImage.BeginAnimation(OpacityProperty, null);
        _currentImage.Opacity = 1;
        _isTransitioning = false;
    }

    private TimeSpan EffectiveDuration
    {
        get
        {
            var duration = TransitionDuration;
            if (duration <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return duration > MaxTransitionDuration ? MaxTransitionDuration : duration;
        }
    }
}
