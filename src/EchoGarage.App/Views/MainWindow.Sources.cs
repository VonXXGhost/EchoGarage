using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EchoGarage.App.ViewModels;

namespace EchoGarage.App.Views;

/// <summary>
/// 左侧“媒体库 / 播放列表”来源列表（SourceList）的右键菜单。
/// 原顶部菜单栏的“播放列表”菜单整体迁移到这里：新建 / 新建智能 / 编辑智能条件 /
/// 重命名 / 删除 / 从播放列表移除所选歌曲 / 添加所选歌曲到其他播放列表。
///
/// 交互约定：
/// - 右键**不会**切换当前来源（<see cref="MainViewModel.SelectedSource"/>）。早期实现为了
///   与资源管理器一致会先选中命中的来源，但选中一个含数千首歌曲的来源要同步重建行视图、
///   排序与分组（实测大库可达 1~3 秒），右键菜单会被阻塞到加载完成才弹出。现在右键只记录
///   命中的来源节点，菜单命令显式作用于该节点，菜单可立即弹出；左键单击仍按原逻辑切换来源。
/// - 右键落在分组标题或列表空白处时仍弹出菜单，但只有两个“新建…”可用。
/// - 菜单可用状态在 ContextMenuOpening（菜单显示前）按命中的来源节点与当前选中歌曲行刷新，
///   因为 MainViewModel 的命令没有 CanExecute（IsEnabled 由这里统一控制）。
/// </summary>
public partial class MainWindow
{
    /// <summary>最近一次右键命中的来源节点；null 表示分组标题 / 空白区域。</summary>
    private SourceNodeViewModel? _sourceListContextNode;

    private void OnSourceListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Right)
        {
            return;
        }

        // 只记录命中节点，不改变 SelectedSource：切换来源会同步重建列表（大库可达数秒），
        // 导致右键菜单迟迟无法弹出。菜单命令改为显式作用于这个节点。
        // 同时拦掉 WPF ListBoxItem 对右键的默认选中行为（它会在右键按下时把项设为选中），
        // 否则 7981 首的“全部音乐”一被右键就会触发整表重建。
        _sourceListContextNode = FindSourceNodeAt(e.GetPosition(SourceList));
        e.Handled = true;
    }

    private void OnSourceListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SourceList.ContextMenu is not { } menu)
        {
            return;
        }

        SourceNodeViewModel? node;
        if (e.CursorLeft < 0 && e.CursorTop < 0)
        {
            // 键盘（Shift+F10 / 菜单键）打开时没有鼠标坐标：目标退回当前选中项。
            node = SourceList.SelectedItem as SourceNodeViewModel;
            _sourceListContextNode = node;
        }
        else
        {
            node = _sourceListContextNode;
        }

        var playlist = node is { Kind: SourceKind.Playlist, Playlist: not null } ? node : null;
        var hasSelectedRows = _vm.SelectedRows.Count > 0 || _vm.SelectedRow is not null;
        var isActiveSource = node is not null && ReferenceEquals(node, _vm.SelectedSource);

        // 智能条件 / 重命名 / 删除都作用于右键命中的播放列表，不要求它当前被选中。
        SetSourceMenuItemEnabled(menu, "EditSmartRules", playlist?.IsSmart == true);
        SetSourceMenuItemEnabled(menu, "RenamePlaylist", playlist?.IsRemovablePlaylist == true);
        SetSourceMenuItemEnabled(menu, "DeletePlaylist", playlist?.IsRemovablePlaylist == true);

        // “从播放列表移除所选歌曲”作用于右侧列表的选中行，因此要求右键命中的就是当前来源，
        // 且它是普通（非智能）播放列表。
        SetSourceMenuItemEnabled(
            menu,
            "RemoveSelected",
            isActiveSource && playlist is { IsSmart: false } && hasSelectedRows);

        // 空白处 / 分组标题右键时只有“新建…”可用：歌曲相关命令要求命中一个具体来源节点。
        SetSourceMenuItemEnabled(menu, "AddSelectedToPlaylist", node is not null && hasSelectedRows);
    }

    private async void OnSourceMenuEditSmartRulesClick(object sender, RoutedEventArgs e) =>
        await _vm.EditSmartRulesFromMenuAsync(_sourceListContextNode);

    private async void OnSourceMenuRenamePlaylistClick(object sender, RoutedEventArgs e) =>
        await _vm.RenamePlaylistFromMenuAsync(_sourceListContextNode);

    private async void OnSourceMenuDeletePlaylistClick(object sender, RoutedEventArgs e) =>
        await _vm.DeletePlaylistFromMenuAsync(_sourceListContextNode);

    /// <summary>命中测试：只有来源项（ListBoxItem）算有效节点，分组标题 / 空白 / 滚动条都返回 null。</summary>
    private SourceNodeViewModel? FindSourceNodeAt(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > SourceList.ActualWidth || point.Y > SourceList.ActualHeight)
        {
            return null;
        }

        var hit = SourceList.InputHitTest(point) as DependencyObject;
        return FindAncestor<ListBoxItem>(hit)?.DataContext as SourceNodeViewModel;
    }

    private static void SetSourceMenuItemEnabled(ContextMenu menu, string tag, bool isEnabled)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                item.IsEnabled = isEnabled;
                return;
            }
        }
    }
}
