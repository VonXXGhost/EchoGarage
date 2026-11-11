using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace EchoGarage.App.Services;

/// <summary>
/// A <see cref="Track"/> that gives the vertical scrollbar thumb a minimum length by arranging the
/// thumb itself into a taller layout slot.
/// </summary>
/// <remarks>
/// Track sizes the thumb as <c>trackLength * ViewportSize / (Maximum - Minimum + ViewportSize)</c>
/// (with its own small lower bound) and arranges the thumb into a slot of that length. Setting
/// <see cref="FrameworkElement.MinHeight"/> on the thumb makes its RenderSize taller than the slot,
/// but WPF applies a layout clip at the slot boundary, so the visible height does not grow. This
/// track computes the slot with the minimum applied and arranges the thumb into it directly, which
/// removes the layout clip. The thumb is placed proportionally between the track ends, so at the
/// end of the range its bottom edge is flush with the end of the track instead of spilling past it.
/// The default 14 DIP slot yields the requested ~2x visible thumb height: the shared thumb
/// template insets the border by 1 DIP on each side, so the painted pill is about 12 DIP tall.
/// </remarks>
public sealed class MinThumbTrack : Track
{
    /// <summary>Minimum length of the vertical thumb in device-independent pixels.</summary>
    public static readonly DependencyProperty MinThumbLengthProperty =
        DependencyProperty.Register(
            nameof(MinThumbLength),
            typeof(double),
            typeof(MinThumbTrack),
            new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Gets or sets the minimum thumb length. Zero disables the minimum.</summary>
    public double MinThumbLength
    {
        get => (double)GetValue(MinThumbLengthProperty);
        set => SetValue(MinThumbLengthProperty, value);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size arrangeSize)
    {
        // Let Track do its normal layout first (repeat buttons and the proportional thumb slot).
        var result = base.ArrangeOverride(arrangeSize);

        if (Orientation != Orientation.Vertical || Thumb is not { } thumb)
        {
            return result;
        }

        var trackLength = arrangeSize.Height;
        var viewport = ViewportSize;
        var range = Maximum - Minimum;
        if (!double.IsFinite(trackLength) || !double.IsFinite(viewport) || !double.IsFinite(range)
            || trackLength <= 0d || range <= 0d || viewport <= 0d || MinThumbLength <= 0d)
        {
            return result;
        }

        // What Track reserved for the thumb; the thumb needs the minimum when this is smaller.
        var computed = trackLength * viewport / (range + viewport);
        var actual = Math.Max(computed, MinThumbLength);
        if (actual <= computed + 0.5d)
        {
            return result;
        }

        // Same interpolation Track uses: at fraction 0 the thumb starts at the top, at fraction 1
        // its bottom edge is flush with the track end. Arranging into this taller slot removes the
        // layout clip that MinHeight alone cannot avoid.
        var fraction = Math.Clamp((Value - Minimum) / range, 0d, 1d);
        var top = fraction * (trackLength - actual);
        thumb.Arrange(new Rect(0d, top, arrangeSize.Width, actual));
        return result;
    }
}
