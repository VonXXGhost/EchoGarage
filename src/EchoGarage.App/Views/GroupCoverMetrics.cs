using System.Windows;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// Inheritable attached property that passes the album cover size from the grouped list down
/// into every album group template.
/// <para>
/// The group template used to read the size and the gutter width with
/// <c>RelativeSource AncestorType=Window</c> bindings. In a virtualized <see cref="System.Windows.Controls.ListView"/>
/// with grouping, group containers are created and measured while they are transiently detached
/// from the visual tree; such ancestor lookups can then stay unresolved. The cover Border fell
/// back to the natural size of the decoded artwork (roughly 512 DIP for a 512 px image) and the
/// layout grew a giant cover in the gutter (reported by the owner on 2026-11-12).
/// </para>
/// <para>
/// An inheritable attached property is applied by the property engine whenever the element is
/// attached to the tree, and value changes reach every descendant (including the template
/// children of a recycled GroupItem), so the cover size is always available. The template binds
/// to it with <c>RelativeSource Self</c>, which needs no tree walk at all. The gutter column is
/// plain <c>Auto</c> and therefore always matches the cover plus its 8 DIP margins.
/// </para>
/// </summary>
public static class GroupCoverMetrics
{
    /// <summary>Album cover edge length in DIP; set once on the grouped ListView.</summary>
    public static readonly DependencyProperty CoverSizeProperty = DependencyProperty.RegisterAttached(
        "CoverSize",
        typeof(double),
        typeof(GroupCoverMetrics),
        new FrameworkPropertyMetadata(
            PlaylistViewLayout.DefaultGroupCoverSize,
            FrameworkPropertyMetadataOptions.Inherits,
            propertyChangedCallback: null,
            coerceValueCallback: CoerceCoverSize));

    public static double GetCoverSize(DependencyObject element) => (double)element.GetValue(CoverSizeProperty);

    public static void SetCoverSize(DependencyObject element, double value) => element.SetValue(CoverSizeProperty, value);

    private static object CoerceCoverSize(DependencyObject d, object baseValue)
    {
        var value = (double)baseValue;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return PlaylistViewLayout.DefaultGroupCoverSize;
        }

        return Math.Clamp(value, PlaylistViewLayout.MinGroupCoverSize, PlaylistViewLayout.MaxGroupCoverSize);
    }
}
