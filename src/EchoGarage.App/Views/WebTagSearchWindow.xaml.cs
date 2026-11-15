using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EchoGarage.App.ViewModels;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Views;

/// <summary>
/// Web 标签搜索窗口（ADR-022）：三步流程 = 搜索专辑 → 选择结果 → 匹配与写入。
/// 视图模型只通过事件请求提示 / 确认 / 关闭；拖拽等纯视图交互留在本文件。
/// </summary>
public partial class WebTagSearchWindow : ThemedWindow
{
    private readonly WebTagSearchViewModel _viewModel;

    private Point _dragStart;
    private LocalFileRow? _dragCandidate;

    public WebTagSearchWindow(
        IReadOnlyList<Track> tracks,
        IWebTagService webTagService,
        ISettingsService settingsService,
        IAppLogger logger)
    {
        InitializeComponent();
        _viewModel = new WebTagSearchViewModel(tracks, webTagService, settingsService, logger);
        DataContext = _viewModel;

        _viewModel.MessageRequested += OnMessageRequested;
        _viewModel.ConfirmRequested += OnConfirmRequested;
        _viewModel.ApplyRequested += OnApplyRequested;
        _viewModel.CancelRequested += OnCancelRequested;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>写入计划；仅在“写入”成功后由 DialogService 读取。</summary>
    public WebTagApplyPlan? Result { get; private set; }

    #region 视图模型事件

    private void OnMessageRequested(object? sender, WebTagMessageRequestedEventArgs e)
    {
        MessageBox.Show(
            this,
            e.Message,
            e.Title,
            MessageBoxButton.OK,
            e.IsError ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    private void OnConfirmRequested(object? sender, WebTagConfirmRequestedEventArgs e)
    {
        e.Confirmed = MessageBox.Show(
            this,
            e.Message,
            e.Title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question) == MessageBoxResult.OK;
    }

    private void OnApplyRequested(object? sender, WebTagApplyRequestedEventArgs e)
    {
        Result = e.Plan;
        CloseWithResult(true);
    }

    private void OnCancelRequested(object? sender, EventArgs e) => CloseWithResult(false);

    private void CloseWithResult(bool dialogResult)
    {
        try
        {
            DialogResult = dialogResult;
        }
        catch (InvalidOperationException)
        {
            // 非模态显示时无法设置 DialogResult（正常路径始终为 ShowDialog）。
            Close();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WebTagSearchViewModel.Step))
        {
            return;
        }

        // 等绑定更新可见性后再转移焦点，避免把焦点设到 Collapsed 的控件上。
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                switch (_viewModel.Step)
                {
                    case 1:
                        KeywordBox.Focus();
                        break;
                    case 2:
                        ResultsGrid.Focus();
                        break;
                    case 3:
                        RemoteGrid.Focus();
                        break;
                }
            }),
            DispatcherPriority.Input);
    }

    #endregion

    #region 窗口生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsStep1)
        {
            KeywordBox.Focus();
            KeywordBox.SelectAll();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.MessageRequested -= OnMessageRequested;
        _viewModel.ConfirmRequested -= OnConfirmRequested;
        _viewModel.ApplyRequested -= OnApplyRequested;
        _viewModel.CancelRequested -= OnCancelRequested;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        // 关闭 / 切换步骤会取消所有未完成的网络请求。
        _viewModel.Shutdown();

        // 字段勾选是全局策略，关闭时保存（fire-and-forget，不阻塞关窗）。
        _ = _viewModel.SaveFieldSelectionAsync();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CancelCommand.Execute(null);
        }
    }

    private void OnKeywordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.SearchCommand.CanExecute(null))
        {
            _viewModel.SearchCommand.Execute(null);
        }
    }

    private void OnManualLinkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.UseManualLinkCommand.CanExecute(null))
        {
            _viewModel.UseManualLinkCommand.Execute(null);
        }
    }

    #endregion

    #region 第二步：双击结果进入匹配

    private void OnResultsGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null)
        {
            // 双击表头 / 空白区域不进入下一步。
            return;
        }

        if (_viewModel.GoToMatchCommand.CanExecute(null))
        {
            _viewModel.GoToMatchCommand.Execute(null);
        }
    }

    #endregion

    #region 第三步：拖拽绑定

    private void OnLocalGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is LocalFileRow file)
        {
            _dragStart = e.GetPosition(this);
            _dragCandidate = file;
        }
    }

    private void OnLocalGridPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var file = _dragCandidate;
        _dragCandidate = null;
        try
        {
            var data = new DataObject();
            data.SetData(typeof(LocalFileRow), file);
            DragDrop.DoDragDrop(LocalGrid, data, DragDropEffects.Move);
        }
        finally
        {
            _viewModel.SetDragOverRow(null);
        }
    }

    private void OnLocalGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => _dragCandidate = null;

    private void OnRemoteGridDragOver(object sender, DragEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (e.Data.GetDataPresent(typeof(LocalFileRow)) && row?.Item is RemoteTrackRow remote)
        {
            e.Effects = DragDropEffects.Move;
            _viewModel.SetDragOverRow(remote);
        }
        else
        {
            e.Effects = DragDropEffects.None;
            _viewModel.SetDragOverRow(null);
        }

        e.Handled = true;
    }

    private void OnRemoteGridDragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
        {
            _viewModel.SetDragOverRow(null);
        }
    }

    private void OnRemoteGridDrop(object sender, DragEventArgs e)
    {
        _viewModel.SetDragOverRow(null);
        e.Handled = true;

        if (e.Data.GetData(typeof(LocalFileRow)) is not LocalFileRow file)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.Item is RemoteTrackRow remote)
        {
            _viewModel.BindLocalToRemote(remote, file);
        }
        else
        {
            _viewModel.NotifyRemoteDropTargetMissing();
        }
    }

    private void OnLocalGridDragOver(object sender, DragEventArgs e)
    {
        _viewModel.SetDragOverRow(null);
        e.Effects = e.Data.GetDataPresent(typeof(LocalFileRow))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnLocalGridDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(typeof(LocalFileRow)) is not LocalFileRow file)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
        {
            _viewModel.UnbindLocalFile(file);
        }
        else
        {
            _viewModel.NotifyLocalDropTargetMissing();
        }
    }

    #endregion

    #region 视觉树

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    #endregion
}
