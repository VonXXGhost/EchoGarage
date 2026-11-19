using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

/// <summary>
/// 单文件响度调整窗口（ADR-028 §3.5）：模态、打开后自动分析源文件。
/// 转换通过 AppHost.Converter 在后台执行；源文件只读，绝不修改。
/// </summary>
public partial class LoudnessAdjustWindow : ThemedWindow
{
    private readonly AppHost _host;
    private readonly IDialogService _dialogs;
    private readonly Track _track;

    /// <summary>设置页配置的默认目标响度；仅作为窗口打开时的初始值。</summary>
    private readonly double _defaultTargetLufs;

    private LoudnessMeasurement? _measurement;
    private LoudnessAdjustmentPlan? _plan;
    private ConversionFormat _format = ConversionFormat.Flac;
    private bool _isLossyFormat;
    private double _ceilingDbTp = LoudnessGain.LosslessCeilingDbTp;
    private int _sourceSampleRate;
    private int _sourceChannels;
    private bool _targetEdited;
    private bool _pathEdited;
    private bool _suppressEvents = true;
    private bool _busy;
    private bool _succeeded;
    private bool _closeRequested;
    private string? _analysisError;
    private string? _lastOutputPath;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _conversionCts;

    public LoudnessAdjustWindow(AppHost host, IDialogService dialogs, Track track)
    {
        _host = host;
        _dialogs = dialogs;
        _track = track;
        _defaultTargetLufs = Math.Clamp(host.Settings.Current.LoudnessDefaultTargetLufs, -60.0, 0.0);

        InitializeComponent();

        Title = "响度调整";
        SourceNameText.Text = track.FileName;
        SourcePathText.Text = track.Path;
        _sourceSampleRate = track.SampleRate;
        _sourceChannels = track.Channels;
        UpdateSourceInfoText();

        MaxHeight = Math.Max(520, SystemParameters.WorkArea.Height - 60);
        SetOutputPath(DefaultOutputPath(_format));
        _pathEdited = false;
        TargetBox.Text = _defaultTargetLufs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        _suppressEvents = false;

        Loaded += OnLoaded;
        Closing += OnClosing;

        UpdateResampleHint();
        UpdatePlan();
        UpdateControls();
    }

    /// <summary>关闭后由 <see cref="LoudnessCoordinator"/> 写入主窗口状态栏的汇总文案。</summary>
    public string? StatusMessage { get; private set; }

    public bool IsBusy => _busy;

    public bool IsCompleted => _succeeded;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await StartAnalysisAsync();
    }

    private async Task StartAnalysisAsync()
    {
        if (_busy)
        {
            return;
        }

        _measurement = null;
        _plan = null;
        _analysisError = null;
        SuccessPanel.Visibility = Visibility.Collapsed;
        ResetMeasurementTexts();

        _analysisCts = new CancellationTokenSource();
        BeginBusy("分析", "取消分析", "正在分析源文件…");

        try
        {
            var progress = new Progress<LoudnessAnalysisProgress>(OnAnalysisProgress);
            var measurement = await _host.Loudness.AnalyzeAsync(_track.Path, progress, _analysisCts.Token);
            _measurement = measurement;
            ApplyMeasurement(measurement);
            _sourceSampleRate = measurement.SampleRate > 0 ? measurement.SampleRate : _track.SampleRate;
            _sourceChannels = measurement.Channels > 0 ? measurement.Channels : _track.Channels;
            UpdateSourceInfoText();
            UpdateResampleHint();
            _targetEdited = false;
            ApplyModeDefaults();
            UpdatePlan();
            _host.Logger.Info(
                $"Loudness adjustment measurement loaded: source={_track.Path}, " +
                $"integrated={FormatLufs(measurement.IntegratedLufs)}, truePeak={FormatTruePeak(measurement.TruePeakDb)}");
        }
        catch (OperationCanceledException)
        {
            _analysisError = "分析已取消。";
            StatusMessage = "响度调整已取消。";
            _host.Logger.Info($"Loudness adjustment analysis cancelled: {_track.Path}");
        }
        catch (Exception ex)
        {
            _analysisError = $"分析失败：{ex.Message}";
            StatusMessage = $"响度调整失败：{ex.Message}";
            _host.Logger.Warn($"Loudness adjustment analysis failed for {_track.Path}: {ex.Message}");
        }
        finally
        {
            _analysisCts?.Dispose();
            _analysisCts = null;
            EndBusy();
        }
    }

    private void OnAnalysisProgress(LoudnessAnalysisProgress progress)
    {
        if (!_busy)
        {
            return;
        }

        if (progress.Fraction >= 0)
        {
            var fraction = Math.Clamp(progress.Fraction, 0, 1);
            AdjustProgress.IsIndeterminate = false;
            AdjustProgress.Value = fraction * 100;
            ProgressPercentText.Text = $"{fraction * 100:0}%";
        }
        else
        {
            AdjustProgress.IsIndeterminate = true;
            ProgressPercentText.Text = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(progress.Stage))
        {
            ProgressLabelText.Text = progress.Stage;
        }
    }

    private void OnConversionProgress(ConversionProgress report)
    {
        if (!_busy)
        {
            return;
        }

        if (report.Fraction >= 0)
        {
            var fraction = Math.Clamp(report.Fraction, 0, 1);
            AdjustProgress.IsIndeterminate = false;
            AdjustProgress.Value = fraction * 100;
            ProgressPercentText.Text = $"{fraction * 100:0}%";
        }
        else
        {
            AdjustProgress.IsIndeterminate = true;
            ProgressPercentText.Text = string.Empty;
        }

        var stage = DescribeConversionStage(report.Stage);
        ProgressLabelText.Text = string.IsNullOrWhiteSpace(report.Detail)
            ? stage
            : $"{stage} · {report.Detail}";
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _succeeded)
        {
            return;
        }

        try
        {
            await StartAdjustmentAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Loudness adjustment failed unexpectedly", ex);
            _dialogs.Error($"响度调整失败：{ex.Message}", "响度调整失败");
            if (_busy)
            {
                EndBusy();
            }
        }
    }

    private async Task StartAdjustmentAsync()
    {
        if (_measurement is not { } measurement)
        {
            return;
        }

        if (!ValidateAndClampTarget(showHint: true))
        {
            return;
        }

        if (_plan is not { } plan)
        {
            return;
        }

        if (!TryResolveOutputPath(out var outputPath))
        {
            return;
        }

        if (File.Exists(outputPath) &&
            !_dialogs.Confirm($"输出文件已存在：\n{outputPath}\n\n是否覆盖？", "确认覆盖"))
        {
            return;
        }

        SetOutputPath(outputPath);

        if (_sourceSampleRate <= 0)
        {
            try
            {
                _sourceSampleRate = await Task.Run(() => _host.Converter.TryProbeSampleRate(_track.Path)) ?? 0;
            }
            catch (Exception ex)
            {
                _host.Logger.Warn($"Loudness adjustment sample rate probe failed: {_track.Path}: {ex.Message}");
            }

            UpdateResampleHint();
        }

        var preset = BuildPreset(_format);
        var downsampled = false;
        if (_isLossyFormat && _sourceSampleRate > ConversionResamplePolicy.LossySampleRateLimit)
        {
            preset = ConversionResamplePolicy.WithDownsample(preset);
            downsampled = true;
        }

        var target = ResolveTargetLufs(measurement);
        var gainDb = plan.AppliedGainDb;
        var startedAt = Stopwatch.StartNew();

        _host.Logger.Info(
            $"Loudness adjustment started: source={_track.Path}, output={outputPath}, format={_format}, " +
            $"measured={FormatLufs(measurement.IntegratedLufs)}, target={target:0.0} LUFS, gain={FormatGain(gainDb)}, " +
            $"lossyCeiling={_isLossyFormat}, downsampled={downsampled}");

        _conversionCts = new CancellationTokenSource();
        BeginBusy("转换", "取消转换", "正在准备转换…");

        try
        {
            var progress = new Progress<ConversionProgress>(OnConversionProgress);
            var request = new ConversionRequest(
                _track.Path,
                outputPath,
                preset,
                _host.Settings.Current.Encoders,
                gainDb,
                EnableDither: true);
            var result = await _host.Converter.ConvertAsync(request, progress, _conversionCts.Token);

            if (!result.Success)
            {
                var reason = string.IsNullOrWhiteSpace(result.Error) ? "未知错误" : result.Error;
                TryDeletePartialOutput(outputPath);
                if (result.Skipped)
                {
                    StatusMessage = $"响度调整未执行：{reason}";
                    _dialogs.Info(reason, "响度调整");
                    _host.Logger.Warn(
                        $"Loudness adjustment skipped: source={_track.Path}, output={outputPath}, reason={reason}");
                }
                else
                {
                    StatusMessage = $"响度调整失败：{reason}";
                    _dialogs.Error(reason, "响度调整失败");
                    _host.Logger.Warn(
                        $"Loudness adjustment failed: source={_track.Path}, output={outputPath}, reason={reason}");
                }

                return;
            }

            _succeeded = true;
            _lastOutputPath = string.IsNullOrWhiteSpace(result.OutputPath) ? outputPath : result.OutputPath;
            ShowSuccess(_lastOutputPath, gainDb, startedAt.Elapsed);

            if (_isLossyFormat)
            {
                await VerifyLossyOutputAsync(_lastOutputPath);
            }

            _host.Logger.Info(
                $"Loudness adjustment completed: source={_track.Path}, output={_lastOutputPath}, " +
                $"measured={FormatLufs(measurement.IntegratedLufs)}, target={target:0.0} LUFS, gain={FormatGain(gainDb)}, " +
                $"elapsed={FormatElapsed(startedAt.Elapsed)}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "响度调整已取消。";
            _host.Logger.Info($"Loudness adjustment cancelled: source={_track.Path}, output={outputPath}");
            TryDeletePartialOutput(outputPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"响度调整失败：{ex.Message}";
            _host.Logger.Error($"Loudness adjustment failed: source={_track.Path}, output={outputPath}", ex);
            _dialogs.Error($"响度调整失败：{ex.Message}", "响度调整失败");
            TryDeletePartialOutput(outputPath);
        }
        finally
        {
            _conversionCts?.Dispose();
            _conversionCts = null;
            EndBusy();
        }
    }

    private async Task VerifyLossyOutputAsync(string outputPath)
    {
        ProgressLabelText.Text = "正在复测输出响度与真峰值…";
        ProgressPercentText.Text = string.Empty;
        AdjustProgress.IsIndeterminate = true;
        CancelOperationButton.Content = "取消";
        CancelOperationButton.IsEnabled = true;
        var token = _conversionCts?.Token ?? CancellationToken.None;

        try
        {
            var measurement = await _host.Loudness.AnalyzeAsync(outputPath, null, token);
            OutputMeasuredText.Text =
                $"输出实测：I {FormatLufs(measurement.IntegratedLufs)} · TP {FormatTruePeak(measurement.TruePeakDb)}";
            OutputMeasuredText.Visibility = Visibility.Visible;

            if (double.IsFinite(measurement.TruePeakDb) && measurement.TruePeakDb > -1.0)
            {
                OvershootText.Text =
                    $"编码过冲：输出真峰值 {measurement.TruePeakDb:0.0} dBTP 超过 -1.0 dBTP，建议降低目标响度后重新导出。";
                OvershootText.Visibility = Visibility.Visible;
                _host.Logger.Warn(
                    $"Lossy loudness output true peak overshoot: output={outputPath}, " +
                    $"truePeak={measurement.TruePeakDb:0.0} dBTP");
            }
        }
        catch (OperationCanceledException)
        {
            OutputMeasuredText.Text = "输出复测已取消。";
            OutputMeasuredText.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            OutputMeasuredText.Text = $"输出复测失败：{ex.Message}";
            OutputMeasuredText.Visibility = Visibility.Visible;
            _host.Logger.Warn($"Lossy loudness output re-measure failed: output={outputPath}, error={ex.Message}");
        }
    }

    private void ShowSuccess(string outputPath, double gainDb, TimeSpan elapsed)
    {
        SuccessPanel.Visibility = Visibility.Visible;
        OutputPathText.Text = outputPath;
        OutputMeasuredText.Text = _isLossyFormat ? "编码完成，正在复测输出响度…" : string.Empty;
        OutputMeasuredText.Visibility = _isLossyFormat ? Visibility.Visible : Visibility.Collapsed;
        OvershootText.Visibility = Visibility.Collapsed;
        RevealOutputButton.IsEnabled = true;
        AdjustProgress.IsIndeterminate = false;
        AdjustProgress.Value = 100;
        ProgressPercentText.Text = "100%";
        StartButton.Content = "已完成";
        StatusMessage = $"响度调整完成：{Path.GetFileName(outputPath)}（{FormatGain(gainDb)}，用时 {FormatElapsed(elapsed)}）";
    }

    private ConversionPreset BuildPreset(ConversionFormat format) => format switch
    {
        ConversionFormat.Flac => new ConversionPreset
        {
            Name = "响度调整（FLAC）",
            Format = ConversionFormat.Flac,
            RateMode = ConversionRateMode.Vbr,
            Quality = 5,
            BitDepth = ConversionBitDepth.Source,
            CopyTags = true,
            CopyCover = true,
        },
        ConversionFormat.Wav => new ConversionPreset
        {
            Name = "响度调整（WAV）",
            Format = ConversionFormat.Wav,
            RateMode = ConversionRateMode.Vbr,
            BitDepth = ConversionBitDepth.Source,
            CopyTags = true,
            CopyCover = true,
        },
        ConversionFormat.Mp3 => new ConversionPreset
        {
            Name = "响度调整（MP3）",
            Format = ConversionFormat.Mp3,
            RateMode = ConversionRateMode.Cbr,
            BitrateKbps = 320,
            BitDepth = ConversionBitDepth.Pcm16,
            CopyTags = true,
            CopyCover = true,
        },
        ConversionFormat.Aac => new ConversionPreset
        {
            Name = "响度调整（AAC）",
            Format = ConversionFormat.Aac,
            RateMode = ConversionRateMode.Vbr,
            Quality = 100,
            BitDepth = ConversionBitDepth.Pcm16,
            CopyTags = true,
            CopyCover = true,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的输出格式。"),
    };

    private void ResetMeasurementTexts()
    {
        IntegratedValueText.Text = "—";
        LoudnessRangeValueText.Text = "—";
        TruePeakValueText.Text = "—";
        SamplePeakValueText.Text = "—";
        CurrentPeakText.Text = "—";
        CeilingText.Text = FormatCeiling();
        MaxBoostText.Text = "—";
        ReachableLufsText.Text = "—";
        AppliedGainText.Text = "—";
        ExpectedOutputText.Text = "—";
        SetTargetText(null);
        SetHint(string.Empty, warning: false);
    }

    private void ApplyMeasurement(LoudnessMeasurement measurement)
    {
        IntegratedValueText.Text = FormatLufs(measurement.IntegratedLufs);
        LoudnessRangeValueText.Text = FormatRange(measurement.LoudnessRange);
        TruePeakValueText.Text = FormatTruePeak(measurement.TruePeakDb);
        SamplePeakValueText.Text = FormatSamplePeak(measurement.SamplePeakDb);
    }

    private void ApplyModeDefaults()
    {
        if (_measurement is not { IntegratedLufs: double integrated } measurement ||
            !double.IsFinite(integrated))
        {
            return;
        }

        if (_targetEdited)
        {
            return;
        }

        if (RaiseRadio.IsChecked == true)
        {
            var maxSafeGain = Math.Max(0.0, LoudnessGain.MaxSafeGainDb(EffectiveTruePeak(measurement), _ceilingDbTp));
            SetTargetText(Math.Min(0.0, Math.Round(integrated + maxSafeGain, 1, MidpointRounding.AwayFromZero)));
        }
        else
        {
            SetTargetText(integrated < _defaultTargetLufs
                ? Math.Round(integrated, 1, MidpointRounding.AwayFromZero)
                : _defaultTargetLufs);
        }
    }

    private bool ValidateAndClampTarget(bool showHint)
    {
        if (_measurement is not { IntegratedLufs: double integrated } measurement ||
            !double.IsFinite(integrated))
        {
            return false;
        }

        if (!TryParseTarget(TargetBox.Text, out var value) || !double.IsFinite(value))
        {
            _plan = null;
            UpdatePlan();
            if (showHint)
            {
                SetHint("请输入有效的目标响度（-60.0 ~ 0.0 LUFS）。", warning: true);
            }

            return false;
        }

        var clamped = Math.Clamp(Math.Round(value, 1, MidpointRounding.AwayFromZero), -60.0, 0.0);
        var notice = string.Empty;
        if (Math.Abs(clamped - value) > 0.0001)
        {
            notice = "目标响度超出范围，已调整到 -60.0 ~ 0.0 LUFS 之间。";
        }

        if (RaiseRadio.IsChecked == true)
        {
            var maxSafeGain = Math.Max(0.0, LoudnessGain.MaxSafeGainDb(EffectiveTruePeak(measurement), _ceilingDbTp));
            var maxTarget = Math.Min(0.0, Math.Round(integrated + maxSafeGain, 1, MidpointRounding.AwayFromZero));
            if (clamped > maxTarget + 0.049)
            {
                clamped = maxTarget;
                notice = $"目标超出无失真提升上限，已调整为 {clamped:0.0} LUFS。";
            }
        }

        SetTargetText(clamped);
        UpdatePlan();
        if (showHint && !string.IsNullOrEmpty(notice))
        {
            SetHint(notice, warning: true);
        }

        return true;
    }

    private double ResolveTargetLufs(LoudnessMeasurement measurement)
    {
        if (TryParseTarget(TargetBox.Text, out var value) && double.IsFinite(value))
        {
            return Math.Clamp(value, -60.0, 0.0);
        }

        return _defaultTargetLufs;
    }

    private void UpdatePlan()
    {
        _plan = null;
        CeilingText.Text = FormatCeiling();

        if (_measurement is not { } measurement ||
            measurement.IntegratedLufs is not double integrated ||
            !double.IsFinite(integrated))
        {
            CurrentPeakText.Text = "—";
            MaxBoostText.Text = "—";
            ReachableLufsText.Text = "—";
            AppliedGainText.Text = "—";
            ExpectedOutputText.Text = "—";
            if (_measurement is not null)
            {
                SetHint("无法测量积分响度（音频过短或为静音），无法计算增益。", warning: true);
            }

            UpdateControls();
            return;
        }

        var truePeak = EffectiveTruePeak(measurement);
        var maxSafeGain = Math.Max(0.0, LoudnessGain.MaxSafeGainDb(truePeak, _ceilingDbTp));
        CurrentPeakText.Text = FormatTruePeak(truePeak);
        MaxBoostText.Text = $"+{maxSafeGain:0.0} dB";
        ReachableLufsText.Text = $"{Math.Min(0.0, integrated + maxSafeGain):0.0} LUFS";

        if (!TryParseTarget(TargetBox.Text, out var target) || !double.IsFinite(target))
        {
            AppliedGainText.Text = "—";
            ExpectedOutputText.Text = "—";
            UpdateControls();
            return;
        }

        target = Math.Clamp(target, -60.0, 0.0);
        var plan = LoudnessGain.Plan(integrated, truePeak, target, _ceilingDbTp);
        _plan = plan;
        AppliedGainText.Text = FormatGain(plan.AppliedGainDb);
        ExpectedOutputText.Text = $"{plan.ResultingLufs:0.0} LUFS / TP {plan.ResultingTruePeakDb:0.0} dBTP";
        UpdateHint(integrated, target, maxSafeGain, plan);
        UpdateControls();
    }

    private void UpdateHint(double integrated, double target, double maxSafeGain, LoudnessAdjustmentPlan plan)
    {
        var raising = RaiseRadio.IsChecked == true;
        if (raising && plan.RequestedGainDb > 0 && !plan.TargetReachable)
        {
            SetHint(
                $"目标 {target:0.0} LUFS 不可达，已按 {_ceilingDbTp:0.0} dBTP 上限给到 {FormatGain(plan.AppliedGainDb)}。",
                warning: true);
        }
        else if (raising && maxSafeGain <= 0.05)
        {
            SetHint("当前真峰值已达到天花板，无法在不失真的前提下提升响度。", warning: true);
        }
        else if (!raising && integrated <= target + 0.049)
        {
            SetHint("当前响度已低于目标，无需降低。", warning: false);
        }
        else if (!raising)
        {
            SetHint("降低响度不受真峰值天花板限制。", warning: false);
        }
        else
        {
            SetHint("提升时会自动把目标限制在无失真上限内，可手动下调目标。", warning: false);
        }
    }

    private void SetHint(string text, bool warning)
    {
        HintText.Text = text;
        if (TryFindResource(warning ? "DangerBrush" : "TextMutedBrush") is Brush brush)
        {
            HintText.Foreground = brush;
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        ApplyModeDefaults();
        UpdatePlan();
    }

    private void OnTargetChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _targetEdited = true;
        if (!_busy)
        {
            UpdatePlan();
        }
    }

    private void OnTargetLostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || _busy || _measurement is null)
        {
            return;
        }

        ValidateAndClampTarget(showHint: true);
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || FormatCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is not string tag || !Enum.TryParse<ConversionFormat>(tag, out var format) || format == _format)
        {
            return;
        }

        _format = format;
        _isLossyFormat = ConversionResamplePolicy.IsLossyFormat(format);
        _ceilingDbTp = LoudnessGain.CeilingDbTp(_isLossyFormat);

        if (!_pathEdited)
        {
            SetOutputPath(DefaultOutputPath(format));
            _pathEdited = false;
        }

        UpdateResampleHint();
        if (!_targetEdited)
        {
            ApplyModeDefaults();
        }

        UpdatePlan();
        UpdateControls();
    }

    private void OnOutputPathChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _pathEdited = true;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _succeeded)
        {
            return;
        }

        var current = (OutputPathBox.Text ?? string.Empty).Trim();
        var initial = string.IsNullOrWhiteSpace(current) ? DefaultOutputPath(_format) : current;
        var chosen = _dialogs.PickSaveFile(BuildFilter(_format), initial);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            return;
        }

        SetOutputPath(chosen);
        _pathEdited = true;
        UpdateControls();
    }

    private void OnCancelOperationClick(object sender, RoutedEventArgs e) => RequestCancelOperation();

    private void OnRetryAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _ = StartAnalysisAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnRevealOutputClick(object sender, RoutedEventArgs e)
    {
        var path = _lastOutputPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Failed to reveal loudness adjustment output '{path}': {ex.Message}");
            _dialogs.Error($"无法打开资源管理器：{ex.Message}", "打开失败");
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy)
        {
            return;
        }

        e.Cancel = true;
        _closeRequested = true;
        RequestCancelOperation();
    }

    private void BeginBusy(string operation, string cancelText, string label)
    {
        _busy = true;
        ProgressPanel.Visibility = Visibility.Visible;
        CancelOperationButton.Content = cancelText;
        CancelOperationButton.IsEnabled = true;
        ProgressLabelText.Text = label;
        ProgressPercentText.Text = string.Empty;
        AdjustProgress.IsIndeterminate = true;
        UpdateControls();
    }

    private void EndBusy()
    {
        _busy = false;
        ProgressPanel.Visibility = Visibility.Collapsed;
        UpdateControls();
        if (_closeRequested)
        {
            Close();
        }
    }

    private void RequestCancelOperation()
    {
        if (!_busy)
        {
            return;
        }

        CancelOperationButton.IsEnabled = false;
        ProgressLabelText.Text = "正在取消，请稍候…";
        ProgressPercentText.Text = string.Empty;
        _analysisCts?.Cancel();
        _conversionCts?.Cancel();
    }

    private void UpdateControls()
    {
        var hasMeasurement = _measurement is { IntegratedLufs: double integrated } && double.IsFinite(integrated);
        var editable = !_busy && !_succeeded;
        var targetEditable = !_succeeded;

        TargetBox.IsEnabled = targetEditable;
        LowerRadio.IsEnabled = targetEditable;
        RaiseRadio.IsEnabled = targetEditable;
        FormatCombo.IsEnabled = editable;
        OutputPathBox.IsEnabled = editable;
        BrowseButton.IsEnabled = editable;
        StartButton.IsEnabled = editable && hasMeasurement && _plan is not null;
        StartButton.Content = _succeeded ? "已完成" : "开始调整";

        ProgressPanel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;

        if (!_busy && !hasMeasurement && _analysisError is not null)
        {
            AnalysisErrorText.Text = _analysisError;
            RetryPanel.Visibility = Visibility.Visible;
        }
        else
        {
            RetryPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSourceInfoText()
    {
        var durationText = _track.DurationMs > 0
            ? DisplayFormatter.Duration(_track.DurationMs)
            : DisplayFormatter.Duration(_track.Duration);
        var parts = new List<string> { $"时长 {durationText}" };

        var format = string.IsNullOrWhiteSpace(_track.FormatDisplay) ? "—" : _track.FormatDisplay;
        parts.Add(format);

        if (_sourceSampleRate > 0)
        {
            parts.Add(FormatSampleRate(_sourceSampleRate));
        }

        if (_sourceChannels > 0)
        {
            parts.Add(_sourceChannels == 2 ? "立体声" : $"{_sourceChannels} 声道");
        }

        SourceInfoText.Text = string.Join(" · ", parts);
    }

    private void UpdateResampleHint()
    {
        if (_isLossyFormat && _sourceSampleRate > ConversionResamplePolicy.LossySampleRateLimit)
        {
            ResampleHintText.Text =
                $"源采样率 {FormatSampleRate(_sourceSampleRate)} 超过 48 kHz，导出为有损格式时会自动降采样到 48 kHz。";
            ResampleHintText.Visibility = Visibility.Visible;
        }
        else
        {
            ResampleHintText.Text = string.Empty;
            ResampleHintText.Visibility = Visibility.Collapsed;
        }
    }

    private bool TryResolveOutputPath(out string outputPath)
    {
        outputPath = string.Empty;
        var text = (OutputPathBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            _dialogs.Error("请填写输出文件路径。", "响度调整");
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(text);
        }
        catch (Exception ex)
        {
            _dialogs.Error($"输出路径无效：{ex.Message}", "响度调整");
            return false;
        }

        var expectedExtension = ConversionNaming.GetExtension(_format);
        if (!string.Equals(Path.GetExtension(fullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            _dialogs.Error($"输出文件扩展名应为 {expectedExtension}（与所选输出格式一致）。", "响度调整");
            return false;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            _dialogs.Error("输出路径缺少文件夹信息。", "响度调整");
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _dialogs.Error($"无法创建输出目录：{ex.Message}", "响度调整");
            return false;
        }

        if (ConversionNaming.PathsEqual(fullPath, _track.Path))
        {
            _dialogs.Error("输出文件不能与源文件相同，请更换文件名或目录。", "响度调整");
            return false;
        }

        outputPath = fullPath;
        return true;
    }

    private void TryDeletePartialOutput(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath) && !ConversionNaming.PathsEqual(outputPath, _track.Path))
            {
                File.Delete(outputPath);
                _host.Logger.Debug($"Removed incomplete loudness adjustment output: {outputPath}");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Failed to remove incomplete loudness adjustment output '{outputPath}': {ex.Message}");
        }
    }

    private string DefaultOutputPath(ConversionFormat format)
    {
        var directory = Path.GetDirectoryName(_track.Path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(_track.Path);
        return Path.Combine(directory, $"{name} (响度调整){ConversionNaming.GetExtension(format)}");
    }

    private void SetOutputPath(string path)
    {
        var previous = _suppressEvents;
        _suppressEvents = true;
        try
        {
            OutputPathBox.Text = path;
            OutputPathBox.CaretIndex = path.Length;
        }
        finally
        {
            _suppressEvents = previous;
        }
    }

    private void SetTargetText(double? value)
    {
        var previous = _suppressEvents;
        _suppressEvents = true;
        try
        {
            TargetBox.Text = value is double number && double.IsFinite(number)
                ? number.ToString("0.0", CultureInfo.CurrentCulture)
                : string.Empty;
        }
        finally
        {
            _suppressEvents = previous;
        }
    }

    private static bool TryParseTarget(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Replace('−', '-').Replace('－', '-').Trim();
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static double EffectiveTruePeak(LoudnessMeasurement measurement)
    {
        if (double.IsFinite(measurement.TruePeakDb))
        {
            return measurement.TruePeakDb;
        }

        if (double.IsFinite(measurement.SamplePeakDb))
        {
            return measurement.SamplePeakDb;
        }

        return 0.0;
    }

    private string FormatCeiling()
        => $"{_ceilingDbTp:0.0} dBTP（{(_isLossyFormat ? "有损输出" : "无损输出")}）";

    private static string FormatLufs(double? value)
        => value is double number && double.IsFinite(number) ? $"{number:0.0} LUFS" : "—";

    private static string FormatRange(double? value)
        => value is double number && double.IsFinite(number) ? $"{number:0.0} LU" : "—";

    private static string FormatTruePeak(double value)
        => double.IsFinite(value) ? $"{value:0.0} dBTP" : "—";

    private static string FormatSamplePeak(double value)
        => double.IsFinite(value) ? $"{value:0.0} dBFS" : "—";

    private static string FormatGain(double gainDb)
    {
        var sign = gainDb >= 0 ? "+" : "-";
        return $"{sign}{Math.Abs(gainDb):0.0} dB";
    }

    private static string FormatSampleRate(int sampleRate)
        => sampleRate <= 0 ? "—" : $"{sampleRate / 1000.0:0.#} kHz";

    private static string FormatElapsed(TimeSpan elapsed)
        => elapsed.TotalMinutes >= 1
            ? $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds} 秒"
            : $"{elapsed.TotalSeconds:0.0} 秒";

    private static string BuildFilter(ConversionFormat format) => format switch
    {
        ConversionFormat.Flac => "FLAC 音频 (*.flac)|*.flac|所有文件 (*.*)|*.*",
        ConversionFormat.Wav => "WAV 音频 (*.wav)|*.wav|所有文件 (*.*)|*.*",
        ConversionFormat.Mp3 => "MP3 音频 (*.mp3)|*.mp3|所有文件 (*.*)|*.*",
        ConversionFormat.Aac => "AAC 音频 (*.m4a)|*.m4a|所有文件 (*.*)|*.*",
        _ => "音频文件 (*.*)|*.*",
    };

    private static string DescribeConversionStage(ConversionStage stage) => stage switch
    {
        ConversionStage.Preparing => "正在准备…",
        ConversionStage.Decoding => "正在解码…",
        ConversionStage.Resampling => "正在重采样…",
        ConversionStage.Encoding => "正在编码…",
        ConversionStage.WritingTags => "正在写入标签…",
        ConversionStage.Done => "转换完成",
        _ => "正在转换…",
    };
}
