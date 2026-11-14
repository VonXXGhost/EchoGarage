using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EchoGarage.App.Services;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.Views;

/// <summary>Editor for conversion presets and the external encoder locations.</summary>
public partial class ConversionSettingsWindow : ThemedWindow
{
    private static readonly ConversionFormat[] Formats =
    [
        ConversionFormat.Aac, ConversionFormat.Flac, ConversionFormat.Mp3, ConversionFormat.Wav
    ];

    private static readonly string[] FormatLabels =
    [
        "AAC (Nero)", "FLAC", "MP3 (LAME)", "WAV"
    ];

    private static readonly ConversionRateMode[] RateModes =
    [
        ConversionRateMode.Vbr, ConversionRateMode.Abr, ConversionRateMode.Cbr
    ];

    private static readonly string[] RateModeLabels =
    [
        "VBR（质量）", "ABR（平均码率）", "CBR（固定码率）"
    ];

    private static readonly ConversionBitDepth[] BitDepths =
    [
        ConversionBitDepth.Source, ConversionBitDepth.Pcm16, ConversionBitDepth.Pcm24
    ];

    private static readonly string[] BitDepthLabels =
    [
        "跟随源", "16-bit", "24-bit"
    ];

    private static readonly ConversionOutputMode[] OutputModes =
    [
        ConversionOutputMode.SourceFolder, ConversionOutputMode.CustomFolder
    ];

    private static readonly string[] OutputModeLabels =
    [
        "源文件所在文件夹", "自定义文件夹"
    ];

    private static readonly ConversionConflictPolicy[] ConflictPolicies =
    [
        ConversionConflictPolicy.Ask, ConversionConflictPolicy.Skip,
        ConversionConflictPolicy.Overwrite, ConversionConflictPolicy.Rename
    ];

    private static readonly string[] ConflictLabels =
    [
        "询问（推荐）", "跳过已存在的文件", "覆盖已存在的文件", "自动重命名"
    ];

    private static readonly ConversionResampleProfile[] ResampleProfiles =
    [
        ConversionResampleProfile.Standard, ConversionResampleProfile.High, ConversionResampleProfile.Insane
    ];

    private static readonly string[] ResampleProfileLabels =
    [
        "标准（快）", "高（默认）", "最高（慢）"
    ];

    private static readonly int[] SampleRates = [0, 44100, 48000, 88200, 96000, 176400, 192000];

    private static readonly string[] SampleRateLabels =
    [
        "跟随源", "44100 Hz", "48000 Hz", "88200 Hz", "96000 Hz", "176400 Hz", "192000 Hz"
    ];

    private readonly IDialogService _dialogs;
    private readonly List<ConversionPreset> _presets;
    private bool _loading;

    public ConversionSettingsWindow(
        IReadOnlyList<ConversionPreset> presets,
        IDialogService dialogs)
    {
        InitializeComponent();
        _dialogs = dialogs;
        _presets = presets.Select(preset => preset.Clone()).ToList();
        if (_presets.Count == 0)
        {
            _presets.Add(ConversionPresetFactory.CreateDefaultPreset());
        }

        PopulateStaticOptions();
        PresetList.ItemsSource = _presets;
        PresetList.SelectedIndex = 0;
    }

    public ConversionSettingsResult? Result { get; private set; }

    private void PopulateStaticOptions()
    {
        FormatBox.ItemsSource = FormatLabels;
        RateModeBox.ItemsSource = RateModeLabels;
        BitDepthBox.ItemsSource = BitDepthLabels;
        OutputModeBox.ItemsSource = OutputModeLabels;
        ConflictBox.ItemsSource = ConflictLabels;
        ProfileBox.ItemsSource = ResampleProfileLabels;
        SampleRateBox.ItemsSource = SampleRateLabels;
    }

    private ConversionPreset? Current => PresetList.SelectedItem as ConversionPreset;

    private void OnPresetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        LoadEditor(Current);
    }

    private void LoadEditor(ConversionPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        _loading = true;
        try
        {
            NameBox.Text = preset.Name;
            FormatBox.SelectedIndex = Array.IndexOf(Formats, preset.Format);
            RateModeBox.SelectedIndex = Array.IndexOf(RateModes, preset.RateMode);
            QualityBox.Text = FormatQualityValue(preset.Quality, preset.Format);
            BitrateBox.Text = preset.BitrateKbps.ToString();
            BitDepthBox.SelectedIndex = Array.IndexOf(BitDepths, preset.BitDepth);
            OutputModeBox.SelectedIndex = Array.IndexOf(OutputModes, preset.OutputMode);
            OutputFolderBox.Text = preset.OutputFolder;
            TemplateBox.Text = preset.FileNameTemplate;
            ConflictBox.SelectedIndex = Array.IndexOf(ConflictPolicies, preset.ConflictPolicy);
            ResampleCheck.IsChecked = preset.Resample;
            SampleRateBox.SelectedIndex = Math.Max(0, Array.IndexOf(SampleRates, preset.TargetSampleRate));
            ProfileBox.SelectedIndex = Array.IndexOf(ResampleProfiles, preset.ResampleProfile);
            CopyTagsCheck.IsChecked = preset.CopyTags;
            CopyCoverCheck.IsChecked = preset.CopyCover;
            HotkeyBox.Text = preset.Hotkey;
            HotkeyGlobalCheck.IsChecked = preset.HotkeyGlobal;
            UpdateFormatUi();
            UpdateOutputModeUi();
            UpdateResampleUi();
        }
        finally
        {
            _loading = false;
        }
    }

    private void CommitEditor()
    {
        var preset = Current;
        if (preset is null)
        {
            return;
        }

        preset.Name = NameBox.Text.Trim();
        if (FormatBox.SelectedIndex >= 0)
        {
            preset.Format = Formats[FormatBox.SelectedIndex];
        }

        if (RateModeBox.SelectedIndex >= 0)
        {
            preset.RateMode = RateModes[RateModeBox.SelectedIndex];
        }

        preset.Quality = ParseQualityValue(QualityBox.Text.Trim(), preset.Format, preset.Quality);
        preset.BitrateKbps = int.TryParse(BitrateBox.Text.Trim(), out var bitrate) ? bitrate : preset.BitrateKbps;
        if (BitDepthBox.SelectedIndex >= 0)
        {
            preset.BitDepth = BitDepths[BitDepthBox.SelectedIndex];
        }

        if (OutputModeBox.SelectedIndex >= 0)
        {
            preset.OutputMode = OutputModes[OutputModeBox.SelectedIndex];
        }

        preset.OutputFolder = OutputFolderBox.Text.Trim();
        preset.FileNameTemplate = TemplateBox.Text.Trim();
        if (ConflictBox.SelectedIndex >= 0)
        {
            preset.ConflictPolicy = ConflictPolicies[ConflictBox.SelectedIndex];
        }

        preset.Resample = ResampleCheck.IsChecked == true;
        preset.TargetSampleRate = SampleRateBox.SelectedIndex >= 0 ? SampleRates[SampleRateBox.SelectedIndex] : 0;
        if (ProfileBox.SelectedIndex >= 0)
        {
            preset.ResampleProfile = ResampleProfiles[ProfileBox.SelectedIndex];
        }

        preset.CopyTags = CopyTagsCheck.IsChecked == true;
        preset.CopyCover = CopyCoverCheck.IsChecked == true;
        preset.Hotkey = HotkeyBox.Text.Trim();
        preset.HotkeyGlobal = HotkeyGlobalCheck.IsChecked == true;
        PresetList.Items.Refresh();
    }

    private void UpdateFormatUi()
    {
        if (FormatBox.SelectedIndex < 0)
        {
            return;
        }

        var format = Formats[FormatBox.SelectedIndex];
        var mode = RateModeBox.SelectedIndex >= 0 ? RateModes[RateModeBox.SelectedIndex] : ConversionRateMode.Vbr;
        var supportsRate = format is ConversionFormat.Aac or ConversionFormat.Mp3;
        var isFlac = format == ConversionFormat.Flac;

        // Only show the settings that actually apply to the selected format / rate mode.
        SetVisible(supportsRate, RateModeLabel, RateModeBox);
        SetVisible(isFlac || (supportsRate && mode == ConversionRateMode.Vbr), QualityLabel, QualityBox);
        QualityLabel.Text = format switch
        {
            ConversionFormat.Aac => "质量 q (0–1)",
            ConversionFormat.Mp3 => "质量 (0–9)",
            ConversionFormat.Flac => "压缩级别 (0–8)",
            _ => "质量"
        };
        SetVisible(supportsRate && mode != ConversionRateMode.Vbr, BitrateLabel, BitrateBox);
        SetVisible(format is ConversionFormat.Flac or ConversionFormat.Wav, BitDepthLabel, BitDepthBox);
    }

    private void UpdateOutputModeUi()
    {
        var customFolder = OutputModeBox.SelectedIndex == 1;
        SetVisible(customFolder, OutputFolderLabel, OutputFolderBox, BrowseOutputFolderButton);
    }

    private void UpdateResampleUi()
    {
        var enabled = ResampleCheck.IsChecked == true;
        SetVisible(enabled, SampleRateLabel, SampleRateBox);
        SetVisible(enabled, ProfileLabel, ProfileBox);
    }

    private static void SetVisible(bool visible, params FrameworkElement[] elements)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in elements)
        {
            element.Visibility = visibility;
        }
    }

    /// <summary>Nero AAC exposes its VBR quality as a q value in the 0.00–1.00 range.</summary>
    private static string FormatQualityValue(int quality, ConversionFormat format) =>
        format == ConversionFormat.Aac
            ? (Math.Clamp(quality, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture)
            : quality.ToString(CultureInfo.InvariantCulture);

    private static int ParseQualityValue(string text, ConversionFormat format, int fallback)
    {
        if (format == ConversionFormat.Aac)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var quality)
                ? (int)Math.Round(Math.Clamp(quality, 0, 1) * 100)
                : fallback;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "TextMutedBrush");
    }

    private void OnFormatSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateFormatUi();
        if (FormatBox.SelectedIndex >= 0 && Current is { } preset)
        {
            QualityBox.Text = FormatQualityValue(preset.Quality, Formats[FormatBox.SelectedIndex]);
        }
    }

    private void OnOutputModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateOutputModeUi();
    }

    private void OnResampleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateResampleUi();
    }

    private void OnNewPresetClick(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var preset = ConversionPresetFactory.CreateDefaultPreset();
        preset.Name = "新预设";
        _presets.Add(preset);
        RefreshList(_presets.Count - 1);
    }

    private void OnDuplicatePresetClick(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var preset = Current?.Clone() ?? ConversionPresetFactory.CreateDefaultPreset();
        preset.Id = Guid.NewGuid().ToString("N");
        preset.Name = string.IsNullOrWhiteSpace(preset.Name) ? "预设副本" : $"{preset.Name} 副本";
        _presets.Add(preset);
        RefreshList(_presets.Count - 1);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (_presets.Count <= 1)
        {
            SetStatus("至少保留一个预设。", error: true);
            return;
        }

        var index = Math.Max(0, PresetList.SelectedIndex);
        _presets.RemoveAt(index);
        RefreshList(Math.Min(index, _presets.Count - 1));
    }

    private void RefreshList(int selectIndex)
    {
        _loading = true;
        try
        {
            PresetList.ItemsSource = null;
            PresetList.ItemsSource = _presets;
            PresetList.SelectedIndex = Math.Clamp(selectIndex, 0, _presets.Count - 1);
        }
        finally
        {
            _loading = false;
        }

        LoadEditor(Current);
    }

    private void OnBrowseOutputFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _dialogs.PickFolder("选择输出文件夹", OutputFolderBox.Text);
        if (!string.IsNullOrEmpty(folder))
        {
            OutputFolderBox.Text = folder;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        CommitEditor();
        var selectedIndex = PresetList.SelectedIndex;
        var presets = ConversionPresetFactory.NormalizeAll(_presets, null, null);
        _presets.Clear();
        _presets.AddRange(presets);
        RefreshList(Math.Clamp(selectedIndex, 0, _presets.Count - 1));
        Result = new ConversionSettingsResult(presets);
        SetStatus("已保存。", error: false);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => DialogResult = Result is not null;

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Saving applies changes in place; closing afterwards should keep them.
        if (DialogResult is null && Result is not null)
        {
            DialogResult = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = Result is not null;
        }
    }
}
