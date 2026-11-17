using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// 左侧来源列表里“播放列表”分组的拖拽排序。单独放在 partial 文件里，避免和主窗口的
/// 布局 / 文件操作 / 播放相关代码混在一起。
///
/// 交互约定：
/// - 只有普通 / 智能播放列表可以拖动；默认播放列表（IsDefault）固定排在最前，不能拖动。
/// - 鼠标按下后移动超过系统拖动阈值（SystemParameters.Minimum*DragDistance）才开始拖拽，
///   避免把单击选中误判成拖动。
/// - 拖动期间在落点处显示一条主题色指示线，松手后把新顺序交给
///   <see cref="MainViewModel.ReorderPlaylistsAsync"/> 持久化。
/// </summary>
public partial class MainWindow
{
    private const string PlaylistDragFormat = "EchoGarage.PlaylistDrag";
    /// <summary>SourceNodeViewModel.Section 对播放列表分组使用的固定分组名。</summary>
    private const string PlaylistSectionName = "播放列表";

    private Point _playlistDragStart;
    private SourceNodeViewModel? _playlistDragCandidate;

    private void OnSourceListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _playlistDragCandidate = null;
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // 只在播放列表项上记录拖动候选；默认播放列表固定首位，不参与拖拽。
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is
            SourceNodeViewModel { Kind: SourceKind.Playlist, IsDefault: false } node)
        {
            _playlistDragCandidate = node;
            _playlistDragStart = e.GetPosition(SourceList);
        }
    }

    private void OnSourceListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_playlistDragCandidate is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _playlistDragCandidate = null;
            return;
        }

        var position = e.GetPosition(SourceList);
        if (Math.Abs(position.X - _playlistDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _playlistDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var node = _playlistDragCandidate;
        _playlistDragCandidate = null;
        try
        {
            var data = new DataObject(PlaylistDragFormat, node);
            DragDrop.DoDragDrop(SourceList, data, DragDropEffects.Move);
        }
        finally
        {
            HidePlaylistDropIndicator();
        }
    }

    private void OnSourceListDragOver(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedPlaylistNode(e))
        {
            return;
        }

        // 阻止事件继续冒泡到主窗口的文件拖放处理（那里会把非文件拖拽的 Effects 设为 None）。
        e.Handled = true;
        var insertionIndex = ResolvePlaylistInsertionIndex(e);
        if (insertionIndex is null)
        {
            e.Effects = DragDropEffects.None;
            HidePlaylistDropIndicator();
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowPlaylistDropIndicator(insertionIndex.Value);
    }

    private void OnSourceListDragLeave(object sender, DragEventArgs e)
    {
        // 在子项之间移动也会触发 DragLeave，只有真正离开列表时才隐藏指示线。
        var position = e.GetPosition(SourceList);
        if (position.X < 0 || position.Y < 0 ||
            position.X > SourceList.ActualWidth || position.Y > SourceList.ActualHeight)
        {
            HidePlaylistDropIndicator();
        }
    }

    private async void OnSourceListDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedPlaylistNode(e, out var draggedNode))
        {
            return;
        }

        e.Handled = true;
        var insertionIndex = ResolvePlaylistInsertionIndex(e);
        HidePlaylistDropIndicator();
        if (insertionIndex is null)
        {
            return;
        }

        var playlistNodes = GetPlaylistNodes();
        var draggedIndex = playlistNodes.IndexOf(draggedNode);
        if (draggedIndex < 0)
        {
            return;
        }

        // insertionIndex 是“插入到原列表的该下标之前”；移除被拖项后下标要左移一位。
        var targetIndex = insertionIndex.Value > draggedIndex
            ? insertionIndex.Value - 1
            : insertionIndex.Value;

        var orderedNodes = playlistNodes.Where(node => !ReferenceEquals(node, draggedNode)).ToList();
        var minimumIndex = 0;
        var defaultIndex = orderedNodes.FindIndex(node => node.IsDefault);
        if (defaultIndex >= 0)
        {
            minimumIndex = defaultIndex + 1;
        }

        targetIndex = Math.Clamp(targetIndex, minimumIndex, orderedNodes.Count);
        if (targetIndex == draggedIndex)
        {
            return;
        }

        orderedNodes.Insert(targetIndex, draggedNode);
        var orderedPlaylists = orderedNodes
            .Select(node => node.Playlist)
            .Where(playlist => playlist is not null)
            .Cast<Playlist>()
            .ToList();
        await _vm.ReorderPlaylistsAsync(orderedPlaylists);
    }

    /// <summary>
    /// 把鼠标位置解析为“插入到播放列表节点列表的哪个下标之前”。
    /// 只接受播放列表分组内部：分组标题、播放列表项的上/下半个区域，以及最后一项下方的空白。
    /// 落在媒体库分组上时返回 null（不执行排序）。
    /// </summary>
    private int? ResolvePlaylistInsertionIndex(DragEventArgs e)
    {
        var playlistNodes = GetPlaylistNodes();
        if (playlistNodes.Count == 0)
        {
            return null;
        }

        var point = e.GetPosition(SourceList);
        if (point.X < 0 || point.Y < 0 || point.X > SourceList.ActualWidth || point.Y > SourceList.ActualHeight)
        {
            return null;
        }

        var hit = SourceList.InputHitTest(point) as DependencyObject;

        if (FindAncestor<ListBoxItem>(hit)?.DataContext is SourceNodeViewModel target)
        {
            if (target.Kind != SourceKind.Playlist || target.Playlist is null)
            {
                return null;
            }

            var targetIndex = playlistNodes.FindIndex(node => ReferenceEquals(node, target));
            if (targetIndex < 0)
            {
                return null;
            }

            // 默认播放列表上面不能插入；落点在下半区则插到它后面，否则也视为插到默认列表之后。
            if (target.IsDefault)
            {
                return targetIndex + 1;
            }

            var item = FindAncestor<ListBoxItem>(hit)!;
            var itemTop = item.TranslatePoint(new Point(0, 0), SourceList).Y;
            var after = point.Y >= itemTop + item.ActualHeight / 2;
            var index = after ? targetIndex + 1 : targetIndex;
            return Math.Max(index, MinimumPlaylistInsertIndex(playlistNodes));
        }

        // 分组标题：落在“播放列表”标题上时插到默认播放列表之后；“媒体库”标题不接受。
        if (FindAncestor<GroupItem>(hit) is { } groupItem)
        {
            var group = groupItem.DataContext as CollectionViewGroup
                        ?? groupItem.Content as CollectionViewGroup;
            if (group is not null)
            {
                return string.Equals(group.Name as string, PlaylistSectionName, StringComparison.Ordinal)
                    ? MinimumPlaylistInsertIndex(playlistNodes)
                    : null;
            }
        }

        // 列表空白区域：在最后一项下方视为追加到末尾。
        var lastItem = FindPlaylistItem(playlistNodes[^1]);
        if (lastItem is not null)
        {
            var lastBottom = lastItem.TranslatePoint(new Point(0, lastItem.ActualHeight), SourceList).Y;
            if (point.Y >= lastBottom)
            {
                return playlistNodes.Count;
            }
        }

        return null;
    }

    private void ShowPlaylistDropIndicator(int insertionIndex)
    {
        var playlistNodes = GetPlaylistNodes();
        double? y = null;
        if (insertionIndex >= 0 && insertionIndex < playlistNodes.Count &&
            FindPlaylistItem(playlistNodes[insertionIndex]) is { } targetItem)
        {
            y = targetItem.TranslatePoint(new Point(0, 0), SidebarRoot).Y;
        }
        else if (playlistNodes.Count > 0 && FindPlaylistItem(playlistNodes[^1]) is { } lastItem)
        {
            y = lastItem.TranslatePoint(new Point(0, lastItem.ActualHeight), SidebarRoot).Y;
        }

        if (y is null)
        {
            SourceDropIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        // 指示线高度 2px：让线中点落在插入边界上，视觉上位于两行之间。
        SourceDropIndicator.Margin = new Thickness(8, Math.Max(0, y.Value - SourceDropIndicator.Height / 2), 8, 0);
        SourceDropIndicator.Visibility = Visibility.Visible;
    }

    private void HidePlaylistDropIndicator() => SourceDropIndicator.Visibility = Visibility.Collapsed;

    private List<SourceNodeViewModel> GetPlaylistNodes() =>
        _vm.Sources
            .Where(source => source.Kind == SourceKind.Playlist && source.Playlist is not null)
            .ToList();

    private static int MinimumPlaylistInsertIndex(List<SourceNodeViewModel> playlistNodes)
    {
        var defaultIndex = playlistNodes.FindIndex(node => node.IsDefault);
        return defaultIndex >= 0 ? defaultIndex + 1 : 0;
    }

    private static bool TryGetDraggedPlaylistNode(DragEventArgs e) =>
        TryGetDraggedPlaylistNode(e, out _);

    private static bool TryGetDraggedPlaylistNode(DragEventArgs e, out SourceNodeViewModel node)
    {
        node = null!;
        if (!e.Data.GetDataPresent(PlaylistDragFormat) ||
            e.Data.GetData(PlaylistDragFormat) is not SourceNodeViewModel
            {
                Kind: SourceKind.Playlist, IsDefault: false, Playlist: not null
            } dragged)
        {
            return false;
        }

        node = dragged;
        return true;
    }

    /// <summary>
    /// 找到某个来源节点对应的 ListBoxItem。来源列表按 Section 分组，容器由 GroupItem 内部生成，
    /// 无法直接通过 SourceList.ItemContainerGenerator 取得，所以在可视树里按 DataContext 匹配。
    /// 侧栏列表很短，容器基本都是已生成的；若已被虚拟化则返回 null（只影响指示线位置，不影响排序）。
    /// </summary>
    private ListBoxItem? FindPlaylistItem(SourceNodeViewModel node)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(SourceList);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is ListBoxItem listBoxItem && ReferenceEquals(listBoxItem.DataContext, node))
            {
                return listBoxItem;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
            {
                stack.Push(VisualTreeHelper.GetChild(current, index));
            }
        }

        return null;
    }
}
