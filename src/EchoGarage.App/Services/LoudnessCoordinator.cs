using System.Diagnostics;
using System.IO;
using System.Windows;
using EchoGarage.App.ViewModels;
using EchoGarage.App.Views;
using EchoGarage.Core.Models;

namespace EchoGarage.App.Services;

/// <summary>
/// 响度计算 / 调整窗口的协调器（ADR-028 §3.4 §3.5 §3.7）。
/// 扫描为非模态单例窗口：选中多个文件按顺序逐个后台分析；
/// 调整窗口为单文件模态窗口，内部通过 <see cref="IAudioConverter"/> 完成增益转换。
/// </summary>
public sealed class LoudnessCoordinator
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly IDialogService _dialogs;
    private readonly Window _owner;

    private LoudnessScanWindow? _scanWindow;
    private List<LoudnessScanRowViewModel>? _scanRows;
    private CancellationTokenSource? _scanCts;
    private bool _scanRunning;
    private bool _adjusting;

    public LoudnessCoordinator(AppHost host, MainViewModel vm, IDialogService dialogs, Window owner)
    {
        _host = host;
        _vm = vm;
        _dialogs = dialogs;
        _owner = owner;
    }

    public bool IsScanning => _scanRunning;

    /// <summary>「响度计算…」：对当前选中的歌曲打开（或激活）非模态扫描窗口并自动开始。</summary>
    public async Task ScanSelectedAsync()
    {
        if (_scanWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            existing.Activate();
            existing.Focus();
            return;
        }

        var rows = BuildScanRows();
        if (rows.Count == 0)
        {
            _dialogs.Info("请先在歌曲列表中选中要计算响度的歌曲。", "响度计算");
            return;
        }

        var window = new LoudnessScanWindow(rows) { Owner = _owner };
        _scanWindow = window;
        _scanRows = rows;
        window.CancelRequested += OnScanCancelRequested;
        window.RescanRequested += OnScanRescanRequested;
        window.Closed += OnScanWindowClosed;
        window.Show();
        await RunScanAsync(window, rows, isRescan: false);
    }

    /// <summary>「响度调整…」：对单个文件打开模态调整窗口（窗口内自动分析）。</summary>
    public Task AdjustSingleAsync(Track track)
    {
        if (track is null || _adjusting)
        {
            return Task.CompletedTask;
        }

        _adjusting = true;
        try
        {
            if (!File.Exists(track.Path))
            {
                _dialogs.Error($"文件不存在：{track.Path}", "响度调整");
                return Task.CompletedTask;
            }

            LoudnessAdjustWindow window;
            try
            {
                window = new LoudnessAdjustWindow(_host, _dialogs, track) { Owner = _owner };
            }
            catch (Exception ex)
            {
                _host.Logger.Error($"Failed to create loudness adjustment window for {track.Path}", ex);
                _dialogs.Error($"无法打开响度调整窗口：{ex.Message}", "响度调整");
                return Task.CompletedTask;
            }

            window.ShowDialog();
            if (!string.IsNullOrWhiteSpace(window.StatusMessage))
            {
                SetStatusSafe(window.StatusMessage);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"Loudness adjustment failed for {track.Path}", ex);
            _dialogs.Error($"响度调整失败：{ex.Message}", "响度调整失败");
        }
        finally
        {
            _adjusting = false;
        }

        return Task.CompletedTask;
    }

    private async Task RunScanAsync(
        LoudnessScanWindow window,
        List<LoudnessScanRowViewModel> rows,
        bool isRescan)
    {
        if (_scanRunning)
        {
            return;
        }

        _scanRunning = true;
        using var cancellation = new CancellationTokenSource();
        _scanCts = cancellation;
        var startedAt = Stopwatch.StartNew();
        var mediaElapsed = TimeSpan.Zero;
        var success = 0;
        var failed = 0;
        var finished = 0;
        var canceled = false;

        window.BeginScan(rows.Count);
        SetStatusSafe($"正在计算响度：{rows.Count} 个文件…");
        _host.Logger.Info($"Loudness scan started: files={rows.Count}, rescan={isRescan}");

        try
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                var row = rows[index];
                row.MarkAnalyzing(index + 1, rows.Count);
                window.ReportFile(index, rows.Count, row.FileName, 0);

                var rowIndex = index;
                var progress = new Progress<LoudnessAnalysisProgress>(report =>
                {
                    var fraction = report.Fraction >= 0 ? report.Fraction : 0;
                    window.ReportFile(rowIndex, rows.Count, row.FileName, fraction);
                });

                try
                {
                    var measurement = await _host.Loudness.AnalyzeAsync(row.Track.Path, progress, cancellation.Token);
                    row.ApplyMeasurement(measurement);
                    mediaElapsed += measurement.Duration > TimeSpan.Zero ? measurement.Duration : row.Track.Duration;
                    success++;
                    finished++;
                }
                catch (OperationCanceledException)
                {
                    row.MarkCanceled();
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    row.MarkFailed(SummarizeError(ex));
                    failed++;
                    finished++;
                    _host.Logger.Warn($"Loudness scan failed: file={row.Track.Path}, error={ex.Message}");
                }

                window.ReportFile(index, rows.Count, row.FileName, 1);
            }
        }
        finally
        {
            canceled = canceled || cancellation.IsCancellationRequested;
            if (canceled)
            {
                foreach (var row in rows)
                {
                    if (row.IsWaiting)
                    {
                        row.MarkCanceled();
                    }
                }
            }

            startedAt.Stop();
            var elapsed = startedAt.Elapsed;
            var speed = elapsed.TotalSeconds > 0.001 && mediaElapsed > TimeSpan.Zero
                ? mediaElapsed.TotalSeconds / elapsed.TotalSeconds
                : 0;

            var summary = canceled
                ? $"已取消（完成 {finished}/{rows.Count}）"
                : $"完成 {success} 个，失败 {failed} 个，用时 {FormatElapsed(elapsed)}，平均 {speed:0.0} 倍实时";

            window.Complete(summary, canceled);
            _scanRunning = false;
            _scanCts = null;

            if (canceled)
            {
                _host.Logger.Info(
                    $"Loudness scan cancelled: files={rows.Count}, finished={finished}, " +
                    $"elapsed={elapsed.TotalSeconds:0.0}s");
            }
            else
            {
                _host.Logger.Info(
                    $"Loudness scan finished: files={rows.Count}, ok={success}, failed={failed}, " +
                    $"elapsed={elapsed.TotalSeconds:0.0}s, avgSpeed={speed:0.0}x");
            }

            var statusMessage = canceled
                ? $"响度计算已取消：完成 {finished}/{rows.Count}。"
                : failed > 0
                    ? $"响度计算完成：成功 {success} 个，失败 {failed} 个。"
                    : $"响度计算完成：{success} 个文件，用时 {FormatElapsed(elapsed)}。";
            SetStatusSafe(statusMessage);
        }
    }

    private void OnScanCancelRequested(object? sender, EventArgs e) => _scanCts?.Cancel();

    private async void OnScanRescanRequested(object? sender, EventArgs e)
    {
        if (_scanRunning || _scanWindow is not { } window || _scanRows is not { } rows)
        {
            return;
        }

        try
        {
            foreach (var row in rows)
            {
                row.Reset();
            }

            await RunScanAsync(window, rows, isRescan: true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Loudness rescan failed", ex);
            _dialogs.Error($"重新计算响度失败：{ex.Message}", "响度计算");
        }
    }

    private void OnScanWindowClosed(object? sender, EventArgs e)
    {
        if (sender is LoudnessScanWindow window)
        {
            window.CancelRequested -= OnScanCancelRequested;
            window.RescanRequested -= OnScanRescanRequested;
            window.Closed -= OnScanWindowClosed;
        }

        if (ReferenceEquals(_scanWindow, sender))
        {
            _scanWindow = null;
            _scanRows = null;
        }

        // 正常情况下窗口在分析期间不会真正关闭（窗口自己会先取消）；这里兜底取消。
        _scanCts?.Cancel();
    }

    private List<LoudnessScanRowViewModel> BuildScanRows()
    {
        var selected = _vm.GetFileOperationTargets();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<LoudnessScanRowViewModel>(selected.Count);
        foreach (var row in selected)
        {
            var track = row.Track;
            if (string.IsNullOrWhiteSpace(track.Path) || !seen.Add(track.Path))
            {
                continue;
            }

            rows.Add(new LoudnessScanRowViewModel(track));
        }

        return rows;
    }

    private void SetStatusSafe(string message)
    {
        if (_owner.Dispatcher.CheckAccess())
        {
            _vm.SetStatus(message);
            return;
        }

        _owner.Dispatcher.BeginInvoke(new Action(() => _vm.SetStatus(message)));
    }

    private static string SummarizeError(Exception exception)
    {
        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = exception.GetType().Name;
        }

        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length > 80 ? string.Concat(message.AsSpan(0, 77), "…") : message;
    }

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}
