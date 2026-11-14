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

        _running = true;
        using var cancellation = new CancellationTokenSource();
        var window = new ConversionProgressWindow(preset.Name, items.Count) { Owner = _owner };
        window.CancelRequested += (_, _) => cancellation.Cancel();
        window.Show();

        var results = new List<ConversionResult>(items.Count);
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
                var progress = new Progress<ConversionProgress>(report =>
                    window.Report(currentIndex, items.Count, Path.GetFileName(item.Track.Path),
                        report.Stage, report.Fraction, report.Detail));

                var result = await _host.Converter.ConvertAsync(
                    new ConversionRequest(item.Track.Path, item.OutputPath, preset, _host.Settings.Current.Encoders),
                    progress,
                    cancellation.Token);
                results.Add(result);

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
            window.Complete();
            _running = false;
        }

        ReportSummary(preset, items.Count, results, plan, cancelled);
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

    private void ReportSummary(
        ConversionPreset preset,
        int total,
        IReadOnlyList<ConversionResult> results,
        ConversionPlan plan,
        bool cancelled)
    {
        var succeeded = results.Count(result => result.Success);
        var failed = results.Where(result => !result.Success && !result.Skipped).ToList();
        var skipped = plan.Items.Count(item => item.Skipped) + results.Count(result => result.Skipped);

        if (cancelled)
        {
            _vm.SetStatus($"转换已取消：成功 {succeeded} 个，失败 {failed.Count} 个，跳过 {skipped} 个。");
            return;
        }

        if (failed.Count > 0)
        {
            var details = string.Join(Environment.NewLine, failed
                .Take(5)
                .Select(result => $"{Path.GetFileName(result.OutputPath ?? string.Empty)}：{result.Error}"));
            if (failed.Count > 5)
            {
                details += Environment.NewLine + $"……共 {failed.Count} 个失败";
            }

            _dialogs.Error($"使用预设“{preset.Name}”转换完成，{failed.Count} 个文件失败。{Environment.NewLine}{Environment.NewLine}{details}");
            _vm.SetStatus($"转换完成：成功 {succeeded} 个，失败 {failed.Count} 个，跳过 {skipped} 个。");
            return;
        }

        _vm.SetStatus($"转换完成：成功 {succeeded} 个，跳过 {skipped} 个。");
        if (succeeded > 0)
        {
            _dialogs.Info($"使用预设“{preset.Name}”转换完成，共 {succeeded} 个文件。");
        }
        else
        {
            _dialogs.Info("没有文件被转换。");
        }
    }
}
