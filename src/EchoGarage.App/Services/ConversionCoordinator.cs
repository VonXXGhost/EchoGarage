using System.IO;
using System.Windows;
using EchoGarage.App.Services;
using EchoGarage.App.ViewModels;
using EchoGarage.App.Views;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Services;

/// <summary>
/// Runs a conversion batch for a preset: plans output paths, resolves conflicts,
/// drives the progress window and reports the result back to the user.
/// </summary>
public sealed class ConversionCoordinator
{
    private readonly AppHost _host;
    private readonly MainViewModel _vm;
    private readonly IDialogService _dialogs;
    private readonly Window _owner;
    private bool _running;
    private ConversionProgressWindow? _window;

    public ConversionCoordinator(AppHost host, MainViewModel vm, IDialogService dialogs, Window owner)
    {
        _host = host;
        _vm = vm;
        _dialogs = dialogs;
        _owner = owner;
    }

    public async Task ConvertWithPresetAsync(string presetId)
    {
        if (_running)
        {
            // 同一时间只允许一个批次；重复触发时把已经在跑的进度窗口带到前台。
            _window?.Activate();
            return;
        }

        var preset = _vm.Settings.ConversionPresets.FirstOrDefault(item => item.Id == presetId);
        if (preset is null)
        {
            return;
        }

        var tracks = _vm.SelectedRows.Select(row => row.Track).ToList();
        if (tracks.Count == 0)
        {
            _dialogs.Info("请先在列表中选择要转换的歌曲。");
            return;
        }

        var plan = ConversionPlanner.Plan(tracks, preset, File.Exists);
        if (plan.ConflictCount > 0)
        {
            var choice = _dialogs.ShowConversionConflict(plan.ConflictCount);
            switch (choice)
            {
                case ConversionConflictChoice.Cancel:
                    return;
                case ConversionConflictChoice.OverwriteAll:
                    ConversionPlanner.ApplyConflictPolicy(plan, ConversionConflictPolicy.Overwrite, File.Exists);
                    break;
                case ConversionConflictChoice.SkipConflicts:
                    ConversionPlanner.ApplyConflictPolicy(plan, ConversionConflictPolicy.Skip, File.Exists);
                    break;
                case ConversionConflictChoice.RenameAll:
                    ConversionPlanner.ApplyConflictPolicy(plan, ConversionConflictPolicy.Rename, File.Exists);
                    break;
            }
        }

        var items = plan.Items.Where(item => !item.Skipped).ToList();
        if (items.Count == 0)
        {
            _dialogs.Info("没有可转换的文件。");
            return;
        }

        var presetsByPath = await ResolveResamplePresetsAsync(preset, items);
        if (presetsByPath is null)
        {
            _vm.SetStatus("转换已取消。");
            return;
        }

        CloseCompletedWindow();

        _running = true;
        using var cancellation = new CancellationTokenSource();

        var rows = new List<ConversionResultRowViewModel>(plan.Items.Count);
        var rowsByItem = new Dictionary<ConversionPlanItem, ConversionResultRowViewModel>();
        foreach (var planItem in plan.Items)
        {
            var row = new ConversionResultRowViewModel(planItem);
            rows.Add(row);
            rowsByItem[planItem] = row;
        }

        var window = new ConversionProgressWindow(preset.Name, rows) { Owner = _owner };
        window.CancelRequested += (_, _) => cancellation.Cancel();
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();

        var results = new List<(ConversionPlanItem Item, ConversionResult Result)>(items.Count);
        var cancelled = false;
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var item = items[index];
                var currentIndex = index;
                rowsByItem[item].MarkRunning();

                var progress = new Progress<ConversionProgress>(report =>
                    window.Report(currentIndex, items.Count, Path.GetFileName(item.Track.Path),
                        report.Stage, report.Fraction, report.Detail));

                var itemPreset = presetsByPath.TryGetValue(item.Track.Path, out var resolvedPreset)
                    ? resolvedPreset
                    : preset;
                var result = await _host.Converter.ConvertAsync(
                    new ConversionRequest(item.Track.Path, item.OutputPath, itemPreset, _host.Settings.Current.Encoders),
                    progress,
                    cancellation.Token);
                results.Add((item, result));
                rowsByItem[item].ApplyResult(result);

                if (cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            if (cancelled)
            {
                foreach (var pendingRow in rows.Where(pendingRow => pendingRow.IsPending))
                {
                    pendingRow.MarkCancelled();
                }
            }

            _running = false;
        }

        ReportSummary(window, preset, results, plan, cancelled);
    }

    /// <summary>
    /// 批次结束后的结果窗口保持打开供查看；下一次转换开始前先关掉上一批的结果窗口，避免窗口堆叠。
    /// </summary>
    private void CloseCompletedWindow()
    {
        var window = _window;
        if (window is null || window.IsRunning)
        {
            return;
        }

        _window = null;
        window.Close();
    }

    /// <summary>
    /// Offers downsampling when a lossy preset would otherwise receive sources above 48 kHz.
    /// Returns null when the user cancels (the batch must not start). The returned map contains
    /// the preset to use for every above-limit item; all other items keep the batch preset.
    /// </summary>
    private async Task<Dictionary<string, ConversionPreset>?> ResolveResamplePresetsAsync(
        ConversionPreset preset,
        IReadOnlyList<ConversionPlanItem> items)
    {
        var presets = new Dictionary<string, ConversionPreset>(StringComparer.OrdinalIgnoreCase);
        if (!ConversionResamplePolicy.IsLossyFormat(preset.Format) ||
            ConversionResamplePolicy.HasExplicitResample(preset))
        {
            return presets;
        }

        _vm.SetStatus($"正在检查 {items.Count} 个文件的采样率…");
        var candidates = await Task.Run(() =>
        {
            var found = new List<(ConversionPlanItem Item, int SampleRate)>();
            foreach (var item in items)
            {
                var sampleRate = _host.Converter.TryProbeSampleRate(item.Track.Path);
                if (sampleRate is int rate && ConversionResamplePolicy.NeedsDownsamplePrompt(preset, rate))
                {
                    found.Add((item, rate));
                }
            }

            return found;
        });

        if (candidates.Count == 0)
        {
            return presets;
        }

        var targetRate = ConversionResamplePolicy.LossySampleRateLimit;
        var first = candidates[0];
        var firstChoice = _dialogs.ShowConversionResamplePrompt(new ConversionResamplePrompt(
            Path.GetFileName(first.Item.Track.Path),
            preset.Format,
            first.SampleRate,
            targetRate));

        if (firstChoice == ConversionResampleChoice.Cancel)
        {
            return null;
        }

        var downsamplePreset = ConversionResamplePolicy.WithDownsample(preset);
        var firstDownsampled = firstChoice == ConversionResampleChoice.Downsample;
        var firstPreset = firstDownsampled ? downsamplePreset : preset;
        presets[first.Item.Track.Path] = firstPreset;

        var remaining = candidates.Skip(1).ToList();
        var sameAsFirstForRemaining = false;
        if (remaining.Count > 0)
        {
            var scopeChoice = _dialogs.ShowConversionResampleScopePrompt(new ConversionResampleScopePrompt(
                remaining.Count,
                remaining.Take(3).Select(entry => Path.GetFileName(entry.Item.Track.Path)).ToList(),
                preset.Format,
                targetRate,
                firstDownsampled));

            if (scopeChoice == ConversionResampleScopeChoice.Cancel)
            {
                return null;
            }

            sameAsFirstForRemaining = scopeChoice == ConversionResampleScopeChoice.SameAsFirst;
            var remainingPreset = sameAsFirstForRemaining
                ? firstPreset
                : (firstDownsampled ? preset : downsamplePreset);
            foreach (var entry in remaining)
            {
                presets[entry.Item.Track.Path] = remainingPreset;
            }
        }

        var remainingDownsampled = remaining.Count > 0 &&
            (sameAsFirstForRemaining ? firstDownsampled : !firstDownsampled);
        var downsampleCount = (firstDownsampled ? 1 : 0) + (remainingDownsampled ? remaining.Count : 0);
        _host.Logger.Info(
            $"Conversion resample decisions: preset={preset.Name}, format={preset.Format}, downsample={downsampleCount}, keepSource={candidates.Count - downsampleCount}.");
        return presets;
    }

    public Task OpenSettingsAsync()
    {
        var result = _dialogs.EditConversionSettings(_vm.Settings.ConversionPresets);
        if (result is not null)
        {
            _vm.ApplyConversionSettings(result.Presets);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 批次结束后把结果写进窗口列表与状态栏。不再弹出完成提示：成功 / 跳过 / 失败
    /// 与失败原因都留在 <see cref="ConversionProgressWindow"/> 的列表里。
    /// </summary>
    private void ReportSummary(
        ConversionProgressWindow window,
        ConversionPreset preset,
        IReadOnlyList<(ConversionPlanItem Item, ConversionResult Result)> results,
        ConversionPlan plan,
        bool cancelled)
    {
        var succeeded = results.Count(entry => entry.Result.Success);
        var failed = results.Count(entry => !entry.Result.Success && !entry.Result.Skipped);
        var skipped = plan.Items.Count(item => item.Skipped) + results.Count(entry => entry.Result.Skipped);

        // 只列出非零项：全成功时不再出现“失败 0 个 / 跳过 0 个”的噪音。
        var parts = new List<string>(3);
        if (succeeded > 0)
        {
            parts.Add($"成功 {succeeded} 个");
        }

        if (failed > 0)
        {
            parts.Add($"失败 {failed} 个");
        }

        if (skipped > 0)
        {
            parts.Add($"跳过 {skipped} 个");
        }

        var detail = parts.Count == 0 ? "没有文件被转换" : string.Join("，", parts);
        var summary = cancelled ? $"已取消 · {detail}。" : $"{detail}。";

        var hint = cancelled
            ? "未转换的文件保持原样，可重新选择后再次转换。"
            : failed > 0
                ? $"预设“{preset.Name}”的结果如上，失败原因见「状态」列。"
                : succeeded > 0
                    ? $"预设“{preset.Name}”的结果如上，输出文件已写入目标位置。"
                    : "没有文件被转换。";

        _host.Logger.Info(
            $"Conversion batch finished: preset={preset.Name}, succeeded={succeeded}, failed={failed}, skipped={skipped}, cancelled={cancelled}.");
        _vm.SetStatus(cancelled ? $"转换已取消：{detail}。" : $"转换完成：{detail}。");
        window.Complete(summary, hint, cancelled);
    }
}
