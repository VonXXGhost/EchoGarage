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
/// - WPF 的 ListBox 默认不会因右键改变选中项，而菜单命令都以 MainViewModel.SelectedSource 为准，
///   所以右键按下时先把鼠标命中的来源节点选中（与资源管理器一致）。
/// - 右键落在分组标题或列表空白处时仍弹出菜单，但只有两个“新建…”可用；
///   此时保留原有选中项不动，避免误清空右侧歌曲列表。
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

        _sourceListContextNode = FindSourceNodeAt(e.GetPosition(SourceList));
        if (_sourceListContextNode is null || ReferenceEquals(SourceList.SelectedItem, _sourceListContextNode))
        {
            return;
        }

        // 右键选中另一个来源（与左键单击语义一致）：命令随后作用于该来源。
        SourceList.SelectedItem = _sourceListContextNode;
    }

    private void OnSourceListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SourceList.ContextMenu is not { } menu)
        {
            return;
        }

        // 键盘（Shift+F10 / 菜单键）打开时没有鼠标坐标，退回当前选中项。
        var node = e.CursorLeft < 0 && e.CursorTop < 0
            ? SourceList.SelectedItem as SourceNodeViewModel
            : _sourceListContextNode;

        var playlist = node is { Kind: SourceKind.Playlist, Playlist: not null } ? node : null;
        var hasSelectedRows = _vm.SelectedRows.Count > 0 || _vm.SelectedRow is not null;

        SetSourceMenuItemEnabled(menu, "EditSmartRules", playlist?.IsSmart == true);
        SetSourceMenuItemEnabled(menu, "RenamePlaylist", playlist?.IsRemovablePlaylist == true);
        SetSourceMenuItemEnabled(menu, "DeletePlaylist", playlist?.IsRemovablePlaylist == true);
        SetSourceMenuItemEnabled(menu, "RemoveSelected", playlist is { IsSmart: false } && hasSelectedRows);

        // 空白处 / 分组标题右键时只有“新建…”可用：歌曲相关命令要求命中一个具体来源节点。
        SetSourceMenuItemEnabled(menu, "AddSelectedToPlaylist", node is not null && hasSelectedRows);
    }

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
