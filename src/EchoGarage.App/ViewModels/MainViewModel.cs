using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly IDialogService _dialogs;
    private readonly Dispatcher _dispatcher;
    private readonly ListCollectionView _tracksView;
    private readonly ListCollectionView _groupedTracksView;
    private readonly ICollectionView _sourcesView;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _watcherTimer;
    private readonly DispatcherTimer _settingsTimer;
    private readonly DispatcherTimer _filterTimer;
    private readonly HashSet<string> _pendingWatchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _selectionGate = new(1, 1);

    private bool _suppressSourceSelection;
    private bool _suppressOutputApply;
    private bool _suppressVolumeSave;
    private bool _suppressThemeApply;
    private bool _isSeeking;
    private bool _isDisposed;
    private bool _refreshingTracks;
    private DateTime _lastTrackRefreshUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private List<TrackRowViewModel>? _shuffleOrder;

    /// <summary>
    /// The source whose tracks and layout are currently shown. This is deliberately separate
    /// from <see cref="SelectedSource"/>: the source ListBox uses a TwoWay SelectedItem binding,
    /// so SelectedSource already points at the *requested* source before the switch has happened.
    /// Layouts must be saved to the source that is still displayed, not to the new one.
    /// </summary>
    private SourceNodeViewModel? _activeSource;
    private CancellationTokenSource? _scanCts;

    // Monotonic token so a slow cover decode cannot overwrite a newer track's artwork.
    private int _nowPlayingCoverVersion;
    private string? _currentCoverPath;

    /// <summary>Subtitle for the current source without any search filter applied.</summary>
    private string _basePlaylistSubtitle = string.Empty;

    /// <summary>Field restrictions from the advanced search dialog; null while using the inline search.</summary>
    private IReadOnlyList<SearchField>? _advancedFields;
    private SearchFieldMode _advancedMode = SearchFieldMode.Any;

    /// <summary>True while the advanced search dialog writes the keyword into <see cref="FilterText"/>.</summary>
    private bool _applyingAdvancedSearch;

    public MainViewModel(AppHost host, IDialogService dialogs, bool waveformModeDiagnostics = false)
    {
        _host = host;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = host.Settings.Current;
        _playMode = Settings.PlaybackMode;
        // 直接赋字段：初始化时不应触发切换回调（否则会写设置并尝试加载空路径）。
        // 诊断开关（--waveform）只覆盖本次进程，因此不改写 Settings.SeekBarMode。
        _isWaveformMode = waveformModeDiagnostics || Settings.SeekBarMode == SeekBarMode.Waveform;

        Sources = [];
        _sourcesView = CollectionViewSource.GetDefaultView(Sources);
        _sourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceNodeViewModel.Section)));

        Tracks = [];
        _tracksView = new ListCollectionView(Tracks);
        TracksView = _tracksView;

        _groupedTracksView = new ListCollectionView(Tracks);
        _groupedTracksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TrackRowViewModel.AlbumGroupKey)));
        GroupedTracksView = _groupedTracksView;

        Devices = [];
        Presets = new ObservableCollection<ColumnPreset>(Settings.ColumnPresets);

        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            OnPositionTick, _dispatcher);
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, OnStatusTimerTick, _dispatcher);
        _statusTimer.Stop();
        _watcherTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(900), DispatcherPriority.Background,
            OnWatcherTick, _dispatcher);
        _watcherTimer.Stop();
        _settingsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background,
            OnSettingsTimerTick, _dispatcher);
        _settingsTimer.Stop();
        // Typing must not re-run the whole filter for every keystroke on large libraries.
        _filterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background,
            OnFilterTimerTick, _dispatcher);
        _filterTimer.Stop();

        _host.Player.StateChanged += OnPlayerStateChanged;
        _host.Player.PlaybackEnded += OnPlaybackEnded;
        _host.Watcher.Changed += OnLibraryFileChanged;

        ShowAlbumPanel = Settings.ShowAlbumPanel;
        _suppressVolumeSave = true;
        Volume = Settings.Volume * 100;
        _suppressVolumeSave = false;
        _suppressThemeApply = true;
        ThemeIndex = Settings.Theme == ThemeKind.Light ? 0 : 1;
        _suppressThemeApply = false;
        OutputDescription = _host.Player.OutputDescription;
        StatusMessage = "就绪";
    }

    public AppSettings Settings { get; }
    public ObservableCollection<SourceNodeViewModel> Sources { get; }
    public ICollectionView SourcesView => _sourcesView;
    public ObservableCollection<TrackRowViewModel> Tracks { get; }
    public ICollectionView TracksView { get; }
    public ICollectionView GroupedTracksView { get; }
    /// <summary>Header cells for the left-anchored part of the grouped list.</summary>
    public ObservableCollection<GroupedColumnHeaderViewModel> GroupedLeftColumns { get; } = [];
    /// <summary>Header cells pinned to the right edge of the grouped list.</summary>
    public ObservableCollection<GroupedColumnHeaderViewModel> GroupedRightColumns { get; } = [];
    public ObservableCollection<AudioDeviceInfo> Devices { get; }
    public ObservableCollection<ColumnPreset> Presets { get; }

    public IReadOnlyList<string> OutputModeOptions { get; } = ["共享模式", "独占模式"];
    public IReadOnlyList<string> ThemeOptions { get; } = ["浅色", "深色"];

    [ObservableProperty]
    private int _themeIndex;

    [RelayCommand]
    private void SetTheme(string? index)
    {
        if (int.TryParse(index, out var value))
        {
            ThemeIndex = value;
        }
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_suppressThemeApply)
        {
            return;
        }

        var theme = value == 0 ? ThemeKind.Light : ThemeKind.Dark;
        Settings.Theme = theme;
        ThemeManager.Apply(theme);
        _host.Logger.Info($"Theme switched to {theme}");
        QueueSettingsSave();
        SetStatus($"界面主题：{(value == 0 ? "浅色" : "深色")}");
    }

    [ObservableProperty]
    private PlaylistViewLayout _currentLayout = ViewLayoutFactory.CreateDefault();

    /// <summary>Album cover size used by the grouped header; the cover column resizes it.</summary>
    [ObservableProperty]
    private double _groupCoverSize = PlaylistViewLayout.DefaultGroupCoverSize;

    /// <summary>Width of the cover gutter in the grouped list (cover + 8px margins).</summary>
    public double GroupCoverColumnWidth => GroupCoverSize + 16;

    partial void OnGroupCoverSizeChanged(double value)
    {
        OnPropertyChanged(nameof(GroupCoverColumnWidth));
        _host.Logger.Debug($"Group cover size: {value:F1}");
    }

    partial void OnCurrentLayoutChanged(PlaylistViewLayout value)
    {
        GroupCoverSize = value.GroupCoverSize;
        OnPropertyChanged(nameof(IsGroupedViewActive));
        _host.Logger.Debug($"Layout changed: groupCoverSize={value.GroupCoverSize:F1}, " +
                           $"coverWidth={value.Columns.FirstOrDefault(c => c.Key == "cover")?.Width:F1}");
        RebuildGroupedView();
    }

    [ObservableProperty]
    private SourceNodeViewModel? _selectedSource;

    [ObservableProperty]
    private TrackRowViewModel? _selectedRow;

    [ObservableProperty]
    private TrackRowViewModel? _currentRow;

    [ObservableProperty]
    private string _currentTitle = "未在播放";

    [ObservableProperty]
    private string _currentSubtitle = string.Empty;

    [ObservableProperty]
    private string _currentInfo = string.Empty;

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _currentCover;

    /// <summary>True while the large now-playing cover is being loaded for the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCoverPlaceholder))]
    private bool _isNowPlayingCoverLoading;

    /// <summary>Shows the note placeholder when a track is playing but no artwork exists.</summary>
    public bool ShowCoverPlaceholder =>
        HasCurrentTrack && !IsNowPlayingCoverLoading && CurrentCover is null;

    partial void OnCurrentCoverChanged(System.Windows.Media.Imaging.BitmapSource? value) =>
        OnPropertyChanged(nameof(ShowCoverPlaceholder));

    [ObservableProperty]
    private string _playlistTitle = "媒体库";

    [ObservableProperty]
    private string _playlistSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    /// <summary>
    /// Slider maximum. Never zero: a Slider with Minimum == Maximum renders its thumb at
    /// the right edge (looks like a full progress bar) even when nothing is playing.
    /// </summary>
    public double SeekMaximum => DurationSeconds > 0 ? DurationSeconds : 1;

    partial void OnDurationSecondsChanged(double value) =>
        OnPropertyChanged(nameof(SeekMaximum));

    /// <summary>ADR-015：进度条形态（滑块 / 整曲波形），全局记忆，切换后立即写设置。</summary>
    [ObservableProperty]
    private bool _isWaveformMode;

    /// <summary>当前曲目的波形快照；渐进分析时会被新的前缀快照整体替换。</summary>
    [ObservableProperty]
    private WaveformPeaks? _currentWaveform;

    private CancellationTokenSource? _waveformCts;
    private int _waveformVersion;

    partial void OnIsWaveformModeChanged(bool value)
    {
        Settings.SeekBarMode = value ? SeekBarMode.Waveform : SeekBarMode.Slider;
        QueueSettingsSave();
        if (value)
        {
            StartWaveformLoad(CurrentRow?.Track.Path);
        }
        else
        {
            // 切回滑块后不再分析：释放后台线程与解码器，也把波形快照丢掉。
            CancelWaveformLoad();
        }
    }

    [ObservableProperty]
    private double _volume = 75;

    [ObservableProperty]
    private bool _isMuted;

    public string VolumeText => $"{Math.Round(Volume)}%";

    public string MuteToolTip => IsMuted ? "取消静音" : "静音";

    /// <summary>Sequential or random playback; persisted in settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeLabel))]
    [NotifyPropertyChangedFor(nameof(PlayModeToolTip))]
    [NotifyPropertyChangedFor(nameof(IsShuffleEnabled))]
    private PlaybackMode _playMode = PlaybackMode.Sequential;

    public bool IsShuffleEnabled => PlayMode == PlaybackMode.Shuffle;

    public string PlayModeLabel => IsShuffleEnabled ? "随机" : "顺序";

    public string PlayModeToolTip => IsShuffleEnabled
        ? "随机播放（点击切换为顺序播放）"
        : "顺序播放（点击切换为随机播放）";

    partial void OnPlayModeChanged(PlaybackMode value)
    {
        // A new mode means the previous shuffled round is meaningless. The next "next" rebuilds it.
        _shuffleOrder = null;
        Settings.PlaybackMode = value;
        QueueSettingsSave();
        SetStatus(value == PlaybackMode.Shuffle ? "已切换为随机播放" : "已切换为顺序播放");
    }

    [RelayCommand]
    private void TogglePlayMode() =>
        PlayMode = PlayMode == PlaybackMode.Shuffle ? PlaybackMode.Sequential : PlaybackMode.Shuffle;

    [ObservableProperty]
    private bool _showAlbumPanel = true;

    [ObservableProperty]
    private bool _groupByAlbum = true;

    /// <summary>
    /// True when the album-grouped list is actually shown. Grouping only makes sense for
    /// an album-oriented sort; any other sort order must stay a faithful flat list
    /// instead of merging songs whose albums are not contiguous.
    /// </summary>
    public bool IsGroupedViewActive =>
        GroupByAlbum && PlaylistSortPolicy.SupportsAlbumGrouping(CurrentLayout.SortColumn);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusHint))]
    private string _statusMessage = "就绪";

    /// <summary>
    /// True while a transient status message (scan progress, import result, error …) should
    /// be shown in the status bar. "就绪" is the resting state and stays hidden: the left
    /// side already shows either the current track or "就绪" itself.
    /// </summary>
    public bool HasStatusHint =>
        !string.IsNullOrWhiteSpace(StatusMessage) &&
        !string.Equals(StatusMessage, "就绪", StringComparison.Ordinal);

    [ObservableProperty]
    private double _scanProgress;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _librarySummary = "媒体库 0 首";

    [ObservableProperty]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private int _outputModeIndex;

    [ObservableProperty]
    private string _outputDescription = "未初始化";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterActive))]
    [NotifyPropertyChangedFor(nameof(EmptyListTitle))]
    [NotifyPropertyChangedFor(nameof(EmptyListHint))]
    private string _filterText = string.Empty;

    /// <summary>True when a search query is narrowing the current list.</summary>
    public bool IsFilterActive => !string.IsNullOrWhiteSpace(FilterText);

    public string EmptyListTitle => IsFilterActive ? "没有匹配的歌曲" : "这里还没有歌曲";

    public string EmptyListHint => IsFilterActive
        ? "换个关键词，或使用高级搜索；按 Esc 清空。"
        : "把音频文件或文件夹拖到窗口，或使用“添加文件 / 添加媒体库文件夹”。";

    /// <summary>
    /// The current list has at least one visible row. Search filtering redefines this so the
    /// empty-state overlay also appears when the query matches nothing.
    /// </summary>
    [ObservableProperty]
    private bool _hasTracks;

    partial void OnFilterTextChanged(string value)
    {
        if (_applyingAdvancedSearch)
        {
            // The advanced search dialog wrote the keyword; keep its field restrictions.
            return;
        }

        // Typing in the inline search box always falls back to the default fields.
        _advancedFields = null;
        _advancedMode = SearchFieldMode.Any;

        // Debounce: only the last keystroke within the interval triggers a refresh.
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        _advancedFields = null;
        _advancedMode = SearchFieldMode.Any;

        if (string.IsNullOrEmpty(FilterText))
        {
            _filterTimer.Stop();
            ApplyFilter();
            return;
        }

        FilterText = string.Empty;
    }

    /// <summary>
    /// The advanced search currently narrowing the list, or null when the inline search box
    /// is in charge. Used to pre-fill the dialog when it is opened again.
    /// </summary>
    public SearchQuery? CurrentAdvancedSearch => _advancedFields is null
        ? null
        : new SearchQuery(FilterText, _advancedFields, _advancedMode);

    [RelayCommand]
    private void OpenAdvancedSearch()
    {
        var result = _dialogs.ShowAdvancedSearch(CurrentAdvancedSearch);
        if (result is not null)
        {
            ApplyAdvancedSearch(result);
        }
    }

    /// <summary>Applies an advanced search; an empty query clears the filter.</summary>
    public void ApplyAdvancedSearch(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
        {
            _advancedFields = null;
            _advancedMode = SearchFieldMode.Any;
            _applyingAdvancedSearch = true;
            try
            {
                FilterText = string.Empty;
            }
            finally
            {
                _applyingAdvancedSearch = false;
            }

            _filterTimer.Stop();
            ApplyFilter();
            return;
        }

        _advancedFields = query.Fields.ToArray();
        _advancedMode = query.Mode;
        _applyingAdvancedSearch = true;
        try
        {
            FilterText = query.Text.Trim();
        }
        finally
        {
            _applyingAdvancedSearch = false;
        }

        _filterTimer.Stop();
        ApplyFilter();
    }

    private void OnFilterTimerTick(object? sender, EventArgs e)
    {
        _filterTimer.Stop();
        ApplyFilter();
    }

    /// <summary>
    /// Applies the current query to both list views as a view filter. The underlying
    /// <see cref="Tracks"/> collection stays complete so row state (covers, selection,
    /// playing indicator) survives search changes.
    /// </summary>
    private void ApplyFilter()
    {
        var query = FilterText;
        var isActive = !string.IsNullOrWhiteSpace(query);
        var fields = _advancedFields ?? SearchQuery.InlineFields;
        var mode = _advancedMode;

        Predicate<object>? predicate = isActive
            ? item => item is TrackRowViewModel row && TrackSearch.Matches(row.Track, fields, mode, query)
            : null;

        _tracksView.Filter = predicate;
        _groupedTracksView.Filter = predicate;

        var visible = isActive
            ? Tracks.Count(row => TrackSearch.Matches(row.Track, fields, mode, query))
            : Tracks.Count;
        _shuffleOrder = null;
        HasTracks = visible > 0;
        PlaylistSubtitle = BuildFilteredSubtitle(visible);
    }

    private string BuildFilteredSubtitle(int visible)
    {
        if (!IsFilterActive)
        {
            return _basePlaylistSubtitle;
        }

        var total = Tracks.Count;
        if (visible == 0)
        {
            return $"没有匹配项 · 共 {total:N0} 首";
        }

        return _advancedFields is null
            ? $"{visible:N0} / {total:N0} 首 · 搜索“{FilterText.Trim()}”"
            : $"{visible:N0} / {total:N0} 首 · 高级搜索";
    }

    public event EventHandler? LayoutChanged;
    public event EventHandler? SortChanged;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CurrentRowChanged;

    /// <summary>
    /// Raised after <see cref="RebuildGroupedView"/> replaced the header/row cell list (layout or
    /// pin change). The window uses it to re-stretch the left flow up to the right-pinned block
    /// before the new cells are rendered.
    /// </summary>
    public event EventHandler? GroupedColumnsRebuilt;

    /// <summary>Raised after the user edited the hotkey table in the settings dialog.</summary>
    public event EventHandler? HotkeysChanged;

    /// <summary>Lets the settings dialog tell the window to re-register the hotkeys.</summary>
    public void NotifyHotkeysChanged() => HotkeysChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised when a conversion preset shortcut should start the converter.</summary>
    public event EventHandler<string>? ConversionPresetRequested;

    /// <summary>Called by the hotkey service when a conversion preset shortcut is pressed.</summary>
    public void RequestConversionPreset(string? presetId)
    {
        if (!string.IsNullOrWhiteSpace(presetId))
        {
            ConversionPresetRequested?.Invoke(this, presetId);
        }
    }

    /// <summary>
    /// Persists presets edited in the converter settings dialog, then lets the hotkey service
    /// pick up the new shortcuts. Encoder paths are edited in the general settings page and are
    /// deliberately left untouched here so saving presets cannot overwrite them.
    /// </summary>
    public void ApplyConversionSettings(IReadOnlyList<ConversionPreset> presets)
    {
        Settings.ConversionPresets = ConversionPresetFactory.NormalizeAll(presets, Settings.Hotkeys, _host.Logger);
        QueueSettingsSave();
        NotifyHotkeysChanged();
        SetStatus("转换器设置已保存。");
    }

    /// <summary>
    /// Set by the main window so layout changes can capture the live DataGrid column
    /// widths/order even when the switch is initiated from the view model.
    /// </summary>
    public Func<PlaylistViewLayout?>? LayoutProvider { get; set; }

    /// <summary>Resolves "Cell.&lt;Column&gt;" templates for the grouped list row cells.</summary>
    public Func<string, System.Windows.DataTemplate?>? CellTemplateProvider { get; set; }

    public string PlayGlyph => IsPlaying ? "⏸" : "▶";

    public bool HasCurrentTrack => CurrentRow is not null || !string.IsNullOrEmpty(CurrentTitle) && CurrentTitle != "未在播放";

    public string CurrentFormatText
    {
        get
        {
            if (CurrentRow is null)
            {
                return string.Empty;
            }

            var track = CurrentRow.Track;
            var parts = new List<string> { track.FormatDisplay };
            var sampleRate = DisplayFormatter.SampleRate(track.SampleRate);
            if (!string.IsNullOrEmpty(sampleRate))
            {
                parts.Add(sampleRate + "Hz");
            }

            var bitrate = DisplayFormatter.Bitrate(track.Bitrate);
            if (!string.IsNullOrEmpty(bitrate))
            {
                parts.Add(bitrate + "bps");
            }

            if (track.Channels > 0)
            {
                parts.Add(track.Channels == 2 ? "立体声" : track.Channels == 1 ? "单声道" : $"{track.Channels} 声道");
            }

            return string.Join(" | ", parts);
        }
    }

    public string CurrentFilePath => CurrentRow?.Track.Path ?? string.Empty;

    /// <summary>
    /// Single-line "♪ title — artist — album" summary for the status bar. Missing tags are
    /// skipped and the resting state falls back to "就绪".
    /// </summary>
    public string NowPlayingStatusText
    {
        get
        {
            if (!HasCurrentTrack)
            {
                return "就绪";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(CurrentTitle) && CurrentTitle != "未在播放")
            {
                parts.Add(CurrentTitle.Trim());
            }

            if (CurrentRow is { } row)
            {
                if (!string.IsNullOrWhiteSpace(row.Track.Artist))
                {
                    parts.Add(row.Track.Artist.Trim());
                }

                if (!string.IsNullOrWhiteSpace(row.Track.Album))
                {
                    parts.Add(row.Track.Album.Trim());
                }
            }

            return parts.Count == 0 ? "就绪" : "♪ " + string.Join(" — ", parts);
        }
    }

    partial void OnCurrentTitleChanged(string value) =>
        OnPropertyChanged(nameof(NowPlayingStatusText));

    partial void OnCurrentSubtitleChanged(string value) =>
        OnPropertyChanged(nameof(NowPlayingStatusText));

    partial void OnCurrentInfoChanged(string value) =>
        OnPropertyChanged(nameof(NowPlayingStatusText));

    partial void OnCurrentRowChanged(TrackRowViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentFormatText));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(HasCurrentTrack));
        OnPropertyChanged(nameof(ShowCoverPlaceholder));
        OnPropertyChanged(nameof(NowPlayingStatusText));
    }

    // =====================================================================
    // Startup
    // =====================================================================
    public async Task InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        await ReloadSourcesAsync();
        _host.Logger.Info($"Sources loaded in {stopwatch.ElapsedMilliseconds} ms.");

        _host.Watcher.Start(await _host.LibraryRepository.GetFoldersAsync());
        _positionTimer.Start();

        await SelectSourceAsync(ResolveStartupSource());
        _host.Logger.Info($"Initial playlist loaded in {stopwatch.ElapsedMilliseconds} ms total.");

        // Older scans could cache rows whose tag read failed (no duration, no album/artist).
        // Those rows are marked stale by the scanner, so a single background scan repairs
        // them even when the user has disabled automatic startup scanning.
        var incomplete = 0;
        try
        {
            incomplete = await _host.LibraryRepository.GetIncompleteTrackCountAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Checking cached metadata quality failed: {ex.Message}");
        }

        if (incomplete > 0)
        {
            _host.Logger.Info($"Metadata repair required for {incomplete} tracks; starting background scan.");
        }

        if (incomplete > 0 || Settings.AutoScanOnStartup)
        {
            _ = ScanLibraryAsync(silent: true, repair: incomplete > 0);
        }
    }

    private SourceNodeViewModel? ResolveStartupSource()
    {
        if (Settings.RestoreLastPlaylist && Settings.LastPlaylistId is { } lastId)
        {
            var last = Sources.FirstOrDefault(s => s.Kind == SourceKind.Playlist && s.Playlist?.Id == lastId);
            if (last is not null)
            {
                return last;
            }
        }

        return Sources.FirstOrDefault();
    }

    public async Task ReloadSourcesPublicAsync() => await ReloadSourcesAsync();

    /// <summary>Used by the developer screenshot switch to show a realistic playing state.</summary>
    public async Task PlayPathForDiagnosticsAsync(string path)
    {
        for (var attempt = 0; attempt < 50 && Tracks.Count == 0; attempt++)
        {
            await Task.Delay(100);
        }

        var normalized = PathUtil.Normalize(path);
        var row = Tracks.FirstOrDefault(t => string.Equals(t.Track.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            await PlayRowAsync(row);
        }
    }

    /// <summary>Used by the developer diagnostics to verify seek behavior without UI input.</summary>
    public void SeekForDiagnostics(double seconds) => CommitSeek(seconds);

    private async Task ReloadSourcesAsync()
    {
        var previous = SelectedSource;
        var folders = await _host.LibraryRepository.GetFoldersAsync();
        var playlists = await _host.PlaylistRepository.GetPlaylistsAsync();
        var folderCounts = await _host.LibraryRepository.GetFolderTrackCountsAsync();
        var playlistCounts = await _host.PlaylistRepository.GetPlaylistCountsAsync(playlists);
        var libraryCount = await _host.LibraryRepository.GetLibraryTrackCountAsync();

        _suppressSourceSelection = true;
        try
        {
            Sources.Clear();
            Sources.Add(new SourceNodeViewModel(SourceKind.Library, "全部音乐") { Count = libraryCount });
            foreach (var folder in folders)
            {
                folderCounts.TryGetValue(folder.Id, out var count);
                Sources.Add(new SourceNodeViewModel(SourceKind.Folder, folder.DisplayName)
                {
                    Folder = folder,
                    Count = count
                });
            }

            foreach (var playlist in playlists)
            {
                playlistCounts.TryGetValue(playlist.Id, out var count);
                Sources.Add(new SourceNodeViewModel(SourceKind.Playlist, playlist.Name)
                {
                    Playlist = playlist,
                    Count = count
                });
            }
        }
        finally
        {
            _suppressSourceSelection = false;
        }

        LibrarySummary = $"媒体库 {libraryCount:N0} 首 · {folders.Count} 个文件夹 · {playlists.Count} 个播放列表";

        if (previous is not null)
        {
            SetSelectedSourceSilently(Sources.FirstOrDefault(s => s.Matches(previous)));
        }

        SetSelectedSourceSilently(SelectedSource ?? Sources.FirstOrDefault());
        if (_activeSource is not null)
        {
            // The source nodes are replaced on every reload; keep tracking the displayed one so
            // layout persistence still reaches the right destination (and its playlist object).
            _activeSource = Sources.FirstOrDefault(s => s.Matches(_activeSource)) ?? SelectedSource;
        }

        _sourcesView.Refresh();
    }

    private void SetSelectedSourceSilently(SourceNodeViewModel? node)
    {
        _suppressSourceSelection = true;
        try
        {
            SelectedSource = node;
        }
        finally
        {
            _suppressSourceSelection = false;
        }
    }

    private async Task UpdateCountsAsync()
    {
        var libraryCount = await _host.LibraryRepository.GetLibraryTrackCountAsync();
        var folderCounts = await _host.LibraryRepository.GetFolderTrackCountsAsync();
        var playlists = Sources.Where(s => s.Kind == SourceKind.Playlist && s.Playlist is not null)
            .Select(s => s.Playlist!)
            .ToList();
        var playlistCounts = await _host.PlaylistRepository.GetPlaylistCountsAsync(playlists);

        var libraryNode = Sources.FirstOrDefault(s => s.Kind == SourceKind.Library);
        if (libraryNode is not null)
        {
            libraryNode.Count = libraryCount;
        }

        foreach (var node in Sources)
        {
            if (node.Kind == SourceKind.Folder && node.Folder is not null &&
                folderCounts.TryGetValue(node.Folder.Id, out var count))
            {
                node.Count = count;
            }
            else if (node.Kind == SourceKind.Playlist && node.Playlist is not null &&
                     playlistCounts.TryGetValue(node.Playlist.Id, out var playlistCount))
            {
                node.Count = playlistCount;
            }
        }

        LibrarySummary = $"媒体库 {libraryCount:N0} 首 · {Sources.Count(s => s.Kind == SourceKind.Folder)} 个文件夹 · " +
                         $"{Sources.Count(s => s.Kind == SourceKind.Playlist)} 个播放列表";
    }

    // =====================================================================
    // Source selection and track loading
    // =====================================================================
    partial void OnSelectedSourceChanged(SourceNodeViewModel? value)
    {
        if (_suppressSourceSelection || value is null)
        {
            return;
        }

        _ = SelectSourceAsync(value);
    }

    public async Task SelectSourceAsync(SourceNodeViewModel? source)
    {
        if (source is null || _isDisposed)
        {
            return;
        }

        await _selectionGate.WaitAsync();
        try
        {
            // On the very first selection there is no previous source/grid layout to persist;
            // doing it anyway would overwrite the saved library layout with the default one
            // (the window has not built its columns yet, so CaptureLayoutFromGrid returns null).
            // Always persist to the source that is still on screen: the TwoWay SelectedItem
            // binding has already replaced SelectedSource with the requested source, and using
            // it here wrote the previous layout into the new source (which made grouping vanish
            // when switching back to the library).
            if (_activeSource is not null)
            {
                PersistCurrentLayout(null, destination: _activeSource);
            }

            SetSelectedSourceSilently(source);

            var tracks = await LoadTracksForSourceAsync(source);
            CurrentLayout = source.Kind == SourceKind.Playlist && source.Playlist is not null
                ? ViewLayoutFactory.Deserialize(source.Playlist.ViewLayoutJson)
                : ViewLayoutFactory.Normalize(Settings.LibraryLayout.Clone());

            ApplyTracks(tracks, source);
            _activeSource = source;
            if (source.Kind == SourceKind.Playlist && source.Playlist is not null)
            {
                Settings.LastPlaylistId = source.Playlist.Id;
            }

            QueueSettingsSave();
            _ = RefreshStaleTracksAsync(force: true);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Selecting source failed", ex);
            SetStatus($"加载失败：{ex.Message}");
        }
        finally
        {
            _selectionGate.Release();
        }
    }

    private async Task<IReadOnlyList<Track>> LoadTracksForSourceAsync(SourceNodeViewModel source)
    {
        return source.Kind switch
        {
            SourceKind.Folder when source.Folder is not null =>
                await _host.LibraryRepository.GetLibraryTracksAsync(source.Folder.Id),
            SourceKind.Playlist when source.Playlist is not null =>
                await _host.PlaylistRepository.GetPlaylistTracksAsync(source.Playlist),
            _ => await _host.LibraryRepository.GetLibraryTracksAsync()
        };
    }

    private void ApplyTracks(IReadOnlyList<Track> tracks, SourceNodeViewModel source)
    {
        var playingPath = CurrentRow?.Track.Path;
        _shuffleOrder = null;
        Tracks.Clear();
        for (var i = 0; i < tracks.Count; i++)
        {
            var row = new TrackRowViewModel(tracks[i], i + 1);
            if (playingPath is not null && string.Equals(tracks[i].Path, playingPath, StringComparison.OrdinalIgnoreCase))
            {
                row.IsPlaying = true;
                CurrentRow = row;
                // The row was rebuilt (for example after a metadata repair scan), so refresh
                // the now-playing panel from the new track data instead of the stale row.
                UpdateNowPlayingMetadata(row);
                BeginNowPlayingCoverLoad(tracks[i], keepExistingCover: true);
            }

            Tracks.Add(row);
        }

        PlaylistTitle = source.Kind switch
        {
            SourceKind.Library => "媒体库",
            SourceKind.Folder => source.Folder?.DisplayName ?? source.Name,
            _ => source.Name
        };
        _basePlaylistSubtitle = BuildSubtitle(source, tracks.Count);
        ApplySortAndGrouping();
        ApplyFilter();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildSubtitle(SourceNodeViewModel source, int count)
    {
        return source.Kind switch
        {
            SourceKind.Library => $"{count:N0} 首歌曲",
            SourceKind.Folder => $"{count:N0} 首歌曲 · {source.Folder?.Path}",
            SourceKind.Playlist when source.Playlist?.IsSmart == true => $"智能播放列表 · {count:N0} 首",
            SourceKind.Playlist => $"{count:N0} 首歌曲",
            _ => $"{count:N0} 首歌曲"
        };
    }

    private void ApplySortAndGrouping()
    {
        var primarySort = BuildPrimarySort();
        var grouped = IsGroupedViewActive;

        // Flat DataGrid view: always the user's chosen order, never grouped.
        _tracksView.GroupDescriptions.Clear();
        ApplySortDescriptions(_tracksView, primarySort);

        // Album list: group only for album-oriented sorts. For any other sort the
        // ListView mirrors the flat order so playback and display stay consistent.
        _groupedTracksView.GroupDescriptions.Clear();
        if (grouped)
        {
            _groupedTracksView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(TrackRowViewModel.AlbumGroupKey)));
            ApplySortDescriptions(_groupedTracksView, BuildGroupedSort(primarySort));
        }
        else
        {
            ApplySortDescriptions(_groupedTracksView, primarySort);
        }

        _tracksView.Refresh();
        _groupedTracksView.Refresh();
        RebuildGroupedView();
    }

    private IReadOnlyList<SortDescription> BuildPrimarySort()
    {
        var hasSort = !string.IsNullOrWhiteSpace(CurrentLayout.SortColumn);
        var sortColumn = hasSort ? CurrentLayout.SortColumn! : PlaylistSortPolicy.DefaultOrder;
        var direction = hasSort && CurrentLayout.SortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        return [new SortDescription(sortColumn, direction)];
    }

    /// <summary>
    /// Sort order of the grouped list: the album-oriented primary sort first, then the
    /// album group key so one album's tracks always stay contiguous, and finally the
    /// disc/track order inside each group (foobar2000-style disc → track ascending).
    /// </summary>
    private List<SortDescription> BuildGroupedSort(IReadOnlyList<SortDescription> primary)
    {
        var sort = primary.ToList();
        var direction = primary[0].Direction;
        sort.Add(new SortDescription(nameof(TrackRowViewModel.AlbumGroupKey), direction));
        if (CurrentLayout.GroupSortByDiscTrack)
        {
            sort.Add(new SortDescription(nameof(TrackRowViewModel.DiscNumberSort), ListSortDirection.Ascending));
            sort.Add(new SortDescription(nameof(TrackRowViewModel.TrackNumberSort), ListSortDirection.Ascending));
        }

        sort.Add(new SortDescription(PlaylistSortPolicy.DefaultOrder, ListSortDirection.Ascending));
        return sort;
    }

    private static void ApplySortDescriptions(ListCollectionView view, IReadOnlyList<SortDescription> descriptions)
    {
        view.SortDescriptions.Clear();
        foreach (var description in descriptions)
        {
            view.SortDescriptions.Add(description);
        }
    }

    /// <summary>
    /// Re-applies the current sort to both views. The DataGrid drops the ItemsSource view's
    /// SortDescriptions when its ItemsSource is rebound during a column rebuild, so the window
    /// calls this right after rebuilding. Also used when only the sort order changed.
    /// </summary>
    public void ReapplySort() => ApplySortAndGrouping();

    /// <summary>
    /// Rebuilds the grouped list's column header cells and the per-row cell list. The
    /// grouped view draws the album cover in a fixed gutter next to the whole group
    /// (foobar2000 style), so the cover column is not part of the row cells.
    /// </summary>
    private void RebuildGroupedView()
    {
        GroupedLeftColumns.Clear();
        GroupedRightColumns.Clear();
        ApplySecondaryTexts();
        if (!IsGroupedViewActive)
        {
            foreach (var row in Tracks)
            {
                row.Cells = [];
                row.RightCells = [];
            }

            GroupedColumnsRebuilt?.Invoke(this, EventArgs.Empty);
            return;
        }

        var sortColumn = CurrentLayout.SortColumn;
        var descending = CurrentLayout.SortDescending;
        var templates = new Dictionary<string, System.Windows.DataTemplate?>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in CurrentLayout.Columns.Where(c => c.Visible))
        {
            if (string.Equals(column.Key, "cover", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var descriptor = ColumnCatalog.Find(column.Key);
            if (descriptor is null)
            {
                continue;
            }

            var template = CellTemplateProvider?.Invoke(column.Key);
            if (template is null)
            {
                continue;
            }

            TrackSortMap.ByKey.TryGetValue(column.Key, out var sortProperty);
            var glyph = sortProperty is not null &&
                        string.Equals(sortColumn, sortProperty, StringComparison.Ordinal)
                ? descending ? "▼" : "▲"
                : string.Empty;
            templates[descriptor.Key] = template;
            var header = new GroupedColumnHeaderViewModel
            {
                Key = column.Key,
                Header = descriptor.Header,
                MinWidth = descriptor.MinWidth,
                Width = Math.Max(descriptor.MinWidth, column.Width),
                SortGlyph = glyph,
                Pin = column.Pin
            };

            if (column.Pin == ColumnPin.Right)
            {
                GroupedRightColumns.Add(header);
            }
            else
            {
                GroupedLeftColumns.Add(header);
            }
        }

        var leftHeaders = GroupedLeftColumns.ToList();
        var rightHeaders = GroupedRightColumns.ToList();
        if (rightHeaders.Count > 0)
        {
            // The trailing pinned column's right edge is the list border; its gripper would
            // detach the block from that edge, so it is not resizable from the outside.
            rightHeaders[^1].ShowResizeGripper = false;

            // The first pinned column owns the boundary line against the left flow: dragging it
            // compresses the last left column instead of moving the pinned block.
            if (leftHeaders.Count > 0)
            {
                rightHeaders[0].ShowLeftBoundaryGripper = true;
            }
        }

        foreach (var row in Tracks)
        {
            row.Cells = leftHeaders.Select(h => new TrackCellViewModel
            {
                Key = h.Key,
                HeaderSource = h,
                Row = row,
                Template = templates[h.Key]
            }).ToList();
            row.RightCells = rightHeaders.Select(h => new TrackCellViewModel
            {
                Key = h.Key,
                HeaderSource = h,
                Row = row,
                Template = templates[h.Key]
            }).ToList();
        }

        GroupedColumnsRebuilt?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Recomputes the optional second row line for every track row from the current layout.
    /// Only visible columns with a non-blank secondary format contribute; when the feature
    /// toggle is off all rows fall back to a single line.
    /// </summary>
    private void ApplySecondaryTexts()
    {
        var formats = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (CurrentLayout.ShowSecondaryInfo)
        {
            foreach (var column in CurrentLayout.Columns.Where(c =>
                         c.Visible && !string.IsNullOrWhiteSpace(c.SecondaryFormat)))
            {
                formats[column.Key] = column.SecondaryFormat;
            }
        }

        foreach (var row in Tracks)
        {
            row.ApplySecondaryFormats(formats);
        }
    }

    /// <summary>
    /// Moves a grouped-view column between the left flow and the right-pinned block and
    /// persists the layout. Called from the grouped header's context menu.
    /// </summary>
    public void SetGroupedColumnPin(string key, ColumnPin pin)
    {
        var column = CurrentLayout.Columns.FirstOrDefault(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            _host.Logger.Debug($"Grouped column pin: column '{key}' is not in the current layout.");
            return;
        }

        if (column.Pin == pin)
        {
            return;
        }

        column.Pin = pin;
        PersistCurrentLayout(CurrentLayout.Clone(), captureFromView: false);
        RebuildGroupedView();
        _host.Logger.Debug($"Grouped column pin: {column.Key}={pin}");
    }

    /// <summary>
    /// Applies a width dragged in the grouped view's column header and persists the layout. Only
    /// the header is updated live (row cells bind to it); the layout itself is written once the
    /// drag finishes, so a long drag does not rebuild the whole grouped view repeatedly.
    /// </summary>
    public void PersistGroupedColumnWidth(string key, double width)
    {
        var column = CurrentLayout.Columns.FirstOrDefault(c =>
            string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            _host.Logger.Debug($"Grouped column width: column '{key}' is not in the current layout.");
            return;
        }

        var descriptor = ColumnCatalog.Find(key);
        column.Width = Math.Max(descriptor?.MinWidth ?? 24, width);
        PersistCurrentLayout(CurrentLayout.Clone(), captureFromView: false);
        _host.Logger.Debug($"Grouped column width: {key}={column.Width:F1}");
    }

    /// <summary>Persists the grouped cover size after the cover gutter was dragged.</summary>
    public void PersistGroupCoverSize()
    {
        CurrentLayout.GroupCoverSize = Math.Clamp(GroupCoverSize,
            PlaylistViewLayout.MinGroupCoverSize, PlaylistViewLayout.MaxGroupCoverSize);
        PersistCurrentLayout(CurrentLayout.Clone(), captureFromView: false);
    }

    partial void OnGroupByAlbumChanged(bool value)
    {
        if (value && !PlaylistSortPolicy.SupportsAlbumGrouping(CurrentLayout.SortColumn))
        {
            // Grouping renders only for an album-oriented sort; switch automatically instead
            // of asking the user to find the (possibly hidden) album / album artist column.
            CurrentLayout.SortColumn = PlaylistSortPolicy.Album;
            CurrentLayout.SortDescending = false;
            PersistCurrentLayout(CurrentLayout.Clone(), captureFromView: false);
            SetStatus("已开启按专辑分组，并切换为按专辑排序。");
        }

        OnPropertyChanged(nameof(IsGroupedViewActive));
        ApplySortAndGrouping();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SortBy(string? sortProperty, bool? descending = null)
    {
        if (string.IsNullOrWhiteSpace(sortProperty))
        {
            return;
        }

        var isSame = string.Equals(CurrentLayout.SortColumn, sortProperty, StringComparison.Ordinal);
        var desc = descending ?? (isSame ? !CurrentLayout.SortDescending : false);
        CurrentLayout.SortColumn = sortProperty;
        CurrentLayout.SortDescending = desc;
        OnPropertyChanged(nameof(IsGroupedViewActive));
        ApplySortAndGrouping();
        PersistCurrentLayout();
        // Also fired for header clicks in the grouped list: the DataGrid behind the flat view
        // must refresh its sort arrows when the sort changes while the grouped list is visible.
        SortChanged?.Invoke(this, EventArgs.Empty);
    }

    // =====================================================================
    // Layout and presets
    // =====================================================================
    public void PersistCurrentLayout() => PersistCurrentLayout(null, captureFromView: true);

    /// <param name="capturedLayout">布局快照；为空且 <paramref name="captureFromView"/> 为真时向窗口取当前列宽/顺序。</param>
    /// <param name="captureFromView">
    /// 应用预设或列设置后必须为 false：此时界面还是旧列，抓取会把旧宽度覆盖回新布局。
    /// </param>
    /// <param name="destination">
    /// 布局要保存到的来源。为空时使用当前实际显示的来源（<see cref="_activeSource"/>），而不是
    /// <see cref="SelectedSource"/>——列表选择绑定会先把 SelectedSource 改成新来源，切换来源时必须
    /// 显式传入旧来源，否则旧布局会被写进新来源。
    /// </param>
    public void PersistCurrentLayout(PlaylistViewLayout? capturedLayout, bool captureFromView = true,
        SourceNodeViewModel? destination = null)
    {
        var target = destination ?? _activeSource ?? SelectedSource;
        _host.Logger.Debug(
            $"Persist layout: captured={(capturedLayout is null ? "null" : "provided")} captureFromView={captureFromView} " +
            $"source={target?.Kind.ToString() ?? "null"} currentGroupCoverSize={CurrentLayout.GroupCoverSize:F1}");

        if (capturedLayout is null && captureFromView)
        {
            capturedLayout = LayoutProvider?.Invoke();
            if (capturedLayout is null)
            {
                // The grid has not been built yet; persisting now would overwrite the saved
                // layout with the in-memory default.
                _host.Logger.Debug("Persist layout skipped: the view has no columns yet.");
                return;
            }
        }

        if (capturedLayout is not null)
        {
            CurrentLayout = ViewLayoutFactory.Normalize(capturedLayout);
        }

        if (target?.Kind == SourceKind.Playlist && target.Playlist is not null)
        {
            var playlist = target.Playlist;
            var serialized = ViewLayoutFactory.Serialize(CurrentLayout);
            // Keep the in-memory node in sync with the database: switching back to this playlist
            // deserializes from the node, not from the repository, and a stale value made the
            // playlist reopen with an old layout (and then overwrite the library layout).
            playlist.ViewLayoutJson = serialized;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _host.PlaylistRepository.UpdatePlaylistLayoutAsync(playlist.Id, serialized);
                }
                catch (Exception ex)
                {
                    _host.Logger.Warn($"Saving playlist layout failed: {ex.Message}");
                }
            });
        }
        else
        {
            Settings.LibraryLayout = CurrentLayout.Clone();
            QueueSettingsSave();
        }
    }

    [RelayCommand]
    private void EditColumns()
    {
        var result = _dialogs.EditColumns(CurrentLayout.Clone(), Presets.ToList());
        if (result is null)
        {
            return;
        }

        CurrentLayout = ViewLayoutFactory.Normalize(result.Layout);
        Settings.ColumnPresets = result.Presets.ToList();
        Presets.Clear();
        foreach (var preset in Settings.ColumnPresets)
        {
            Presets.Add(preset);
        }

        PersistCurrentLayout(null, captureFromView: false);
        ApplySortAndGrouping();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        QueueSettingsSave();
    }

    public void ApplyPreset(ColumnPreset preset)
    {
        // Presets describe columns only; keep the user's current grouped-cover size, the
        // per-group disc/track ordering preference and the second-line display switch.
        var applied = preset.ToLayout();
        applied.GroupCoverSize = CurrentLayout.GroupCoverSize;
        applied.GroupSortByDiscTrack = CurrentLayout.GroupSortByDiscTrack;
        applied.ShowSecondaryInfo = CurrentLayout.ShowSecondaryInfo;
        CurrentLayout = ViewLayoutFactory.Normalize(applied);
        ApplySortAndGrouping();
        PersistCurrentLayout(null, captureFromView: false);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        SetStatus($"已应用显示预设：{preset.Name}");
    }

    public void SaveCurrentLayoutAsPreset(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        CurrentLayout.PresetName = name;
        var preset = ColumnPreset.FromLayout(name, CurrentLayout);
        var existing = Settings.ColumnPresets.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            Settings.ColumnPresets.Remove(existing);
        }

        Settings.ColumnPresets.Add(preset);
        Presets.Clear();
        foreach (var item in Settings.ColumnPresets)
        {
            Presets.Add(item);
        }

        PersistCurrentLayout(null, captureFromView: false);
        QueueSettingsSave();
        SetStatus($"已保存显示预设：{name}");
    }

    // =====================================================================
    // Playback
    // =====================================================================
    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        try
        {
            if (_host.Player.State == AudioPlaybackState.Playing)
            {
                _host.Player.Pause();
                return;
            }

            if (_host.Player.State == AudioPlaybackState.Paused)
            {
                _host.Player.Play();
                return;
            }

            // Stopped (either by the user or by reaching the end): restart the current track.
            if (CurrentRow is not null && CurrentRow.IsPlayable)
            {
                await PlayRowAsync(CurrentRow);
                return;
            }

            var target = SelectedRow ?? GetPlaybackOrder().FirstOrDefault();
            if (target is not null)
            {
                await PlayRowAsync(target);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"播放失败：{ex.Message}");
            _host.Logger.Error("PlayPause failed", ex);
        }
    }

    [RelayCommand]
    private async Task PlayRowAsync(TrackRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!row.IsPlayable)
        {
            SetStatus($"文件不存在或无法访问：{row.Track.Path}");
            return;
        }

        if (CurrentRow is not null && !ReferenceEquals(CurrentRow, row))
        {
            CurrentRow.IsPlaying = false;
        }

        CurrentRow = row;
        row.IsPlaying = true;
        CurrentRowChanged?.Invoke(this, EventArgs.Empty);
        UpdateNowPlayingMetadata(row);
        BeginNowPlayingCoverLoad(row.Track);
        DurationSeconds = row.Track.DurationMs / 1000.0;
        DurationText = DisplayFormatter.Duration(row.Track.DurationMs);
        PositionSeconds = 0;
        // 切歌立刻丢弃上一首的波形；新曲目的分析在解码成功后再开始，避免和加载抢 IO。
        CancelWaveformLoad();

        try
        {
            await _host.Player.LoadAsync(row.Track.Path, playWhenReady: true);
            IsPlaying = true;
            OutputDescription = _host.Player.OutputDescription;
            _consecutiveFailures = 0;
            SetStatus($"正在播放：{row.Title}");
            _ = Task.Run(async () =>
            {
                try
                {
                    await _host.LibraryRepository.RecordPlaybackAsync(row.Track.Path);
                }
                catch (Exception ex)
                {
                    _host.Logger.Debug($"Playback counter update failed: {ex.Message}");
                }
            });
            _ = LoadRowCoverAsync(row);
            // ADR-015：播放真正开始后才让分析器排队；服务在解码前还会等约 350 ms 给播放让路。
            StartWaveformLoad(row.Track.Path);
        }
        catch (Exception ex)
        {
            IsPlaying = false;
            _host.Logger.Error($"Cannot play {row.Track.Path}", ex);
            SetStatus($"无法播放 {row.FileName}：{ex.Message}");
            _consecutiveFailures++;
            if (_consecutiveFailures <= 3)
            {
                await PlayNextAsync();
            }
        }
    }

    [RelayCommand]
    private async Task PlayNextAsync()
    {
        if (PlayMode == PlaybackMode.Shuffle)
        {
            await PlayNextShuffleAsync();
            return;
        }

        var order = GetPlaybackOrder();
        if (order.Count == 0)
        {
            return;
        }

        var index = CurrentRow is null ? -1 : order.IndexOf(CurrentRow);
        if (index < 0 || index + 1 >= order.Count)
        {
            _host.Player.Stop();
            IsPlaying = false;
            return;
        }

        await PlayRowAsync(order[index + 1]);
    }

    /// <summary>
    /// Random playback: walks one shuffled round of the current list. When the round ends a
    /// fresh round is shuffled (the track that just finished is left out of the first slot so
    /// it is not repeated immediately), so shuffle keeps playing until the user stops it.
    /// </summary>
    private async Task PlayNextShuffleAsync()
    {
        var playable = GetPlaybackOrder();
        if (playable.Count == 0)
        {
            return;
        }

        if (_shuffleOrder is null || _shuffleOrder.Count != playable.Count)
        {
            _shuffleOrder = ShuffleOrder.BuildRound(playable, CurrentRow);
        }

        var index = CurrentRow is null ? -1 : _shuffleOrder.IndexOf(CurrentRow);
        if (index < 0)
        {
            // The current track is not part of the round (playback was started elsewhere):
            // rebuild the round around it and continue with the rest.
            _shuffleOrder = ShuffleOrder.BuildRound(playable, CurrentRow);
            index = CurrentRow is null ? -1 : _shuffleOrder.IndexOf(CurrentRow);
        }

        if (index + 1 < _shuffleOrder.Count)
        {
            await PlayRowAsync(_shuffleOrder[index + 1]);
            return;
        }

        var nextRound = _shuffleOrder.Where(r => !ReferenceEquals(r, CurrentRow)).ToList();
        if (nextRound.Count == 0)
        {
            _host.Player.Stop();
            IsPlaying = false;
            return;
        }

        _shuffleOrder = ShuffleOrder.BuildRound(nextRound, null);
        await PlayRowAsync(_shuffleOrder[0]);
    }

    [RelayCommand]
    private async Task PlayPreviousAsync()
    {
        if (PlayMode == PlaybackMode.Shuffle && _shuffleOrder is { } shuffled && CurrentRow is { } current)
        {
            var shuffleIndex = shuffled.IndexOf(current);
            if (shuffleIndex > 0)
            {
                await PlayRowAsync(shuffled[shuffleIndex - 1]);
                return;
            }

            if (shuffleIndex == 0)
            {
                // First track of the round: restart it, like sequential mode at the list start.
                await PlayRowAsync(current);
                return;
            }
        }

        var order = GetPlaybackOrder();
        if (order.Count == 0)
        {
            return;
        }

        var index = CurrentRow is null ? -1 : order.IndexOf(CurrentRow);
        if (index <= 0)
        {
            if (CurrentRow is not null)
            {
                await PlayRowAsync(CurrentRow);
            }

            return;
        }

        await PlayRowAsync(order[index - 1]);
    }

    [RelayCommand]
    private void Stop()
    {
        _host.Player.Stop();
        IsPlaying = false;
        PositionSeconds = 0;
        PositionText = "0:00";
        if (CurrentRow is not null)
        {
            CurrentRow.IsPlaying = false;
        }

        CurrentRow = null;
        CurrentRowChanged?.Invoke(this, EventArgs.Empty);
        CancelWaveformLoad();
        CurrentTitle = "未在播放";
        CurrentSubtitle = string.Empty;
        CurrentInfo = string.Empty;
        // Invalidate any in-flight cover decode and drop the artwork right away.
        _nowPlayingCoverVersion++;
        _currentCoverPath = null;
        IsNowPlayingCoverLoading = false;
        CurrentCover = null;
        OnPropertyChanged(nameof(HasCurrentTrack));
    }

    private List<TrackRowViewModel> GetPlaybackOrder()
    {
        var view = IsGroupedViewActive ? (ICollectionView)_groupedTracksView : _tracksView;
        return view.Cast<TrackRowViewModel>().Where(r => r.IsPlayable).ToList();
    }

    private void UpdateNowPlayingMetadata(TrackRowViewModel row)
    {
        CurrentTitle = row.Title;
        CurrentSubtitle = string.IsNullOrWhiteSpace(row.Artist) ? "未知艺术家" : row.Artist;
        var track = row.Track;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            parts.Add(track.Album);
        }

        if (track.Year is > 0)
        {
            parts.Add(track.Year.Value.ToString());
        }

        CurrentInfo = string.Join(" · ", parts);
        DurationSeconds = track.DurationMs / 1000.0;
        DurationText = DisplayFormatter.Duration(track.DurationMs);
    }

    /// <summary>
    /// Starts loading the now-playing cover. The previous track's artwork is removed
    /// immediately, so switching songs never shows the old cover while the new one decodes.
    /// </summary>
    private void BeginNowPlayingCoverLoad(Track track, bool keepExistingCover = false)
    {
        var version = ++_nowPlayingCoverVersion;
        var isSameTrack = string.Equals(_currentCoverPath, track.Path, StringComparison.OrdinalIgnoreCase);
        if (!keepExistingCover && !isSameTrack)
        {
            CurrentCover = null;
            _currentCoverPath = null;
        }

        if (!keepExistingCover || CurrentCover is null)
        {
            IsNowPlayingCoverLoading = true;
        }

        _ = LoadNowPlayingCoverAsync(track, version);
    }

    private async Task LoadNowPlayingCoverAsync(Track track, int version)
    {
        try
        {
            _host.Logger.Debug($"Now playing cover load start: {track.Path}");
            var image = await _host.CoverImages.GetAsync(new CoverRequest(track.Path, track.CoverKey ?? string.Empty,
                track.ModifiedAtUtc, track.FileSize), 420);
            if (version != _nowPlayingCoverVersion ||
                !string.Equals(CurrentRow?.Track.Path, track.Path, StringComparison.OrdinalIgnoreCase))
            {
                _host.Logger.Debug($"Now playing cover discarded (stale): {track.Path}");
                return;
            }

            CurrentCover = image;
            _currentCoverPath = image is null ? null : track.Path;
            _host.Logger.Debug($"Now playing cover loaded: {track.Path} ({(image is null ? "none" : "ok")})");
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Now playing cover failed: {ex.Message}");
        }
        finally
        {
            // Only the most recent request may clear the loading state.
            if (version == _nowPlayingCoverVersion)
            {
                IsNowPlayingCoverLoading = false;
            }
        }
    }

    /// <summary>
    /// Loads the larger cover used by album group headers. 512px keeps the art sharp at
    /// the maximum grouped cover size (320 DIP) even on 150% DPI, and the cache is bounded.
    /// </summary>
    public async Task LoadGroupCoverAsync(TrackRowViewModel row)
    {
        if (row.GroupCoverImage is not null)
        {
            return;
        }

        var track = row.Track;
        if (string.IsNullOrWhiteSpace(track.CoverKey))
        {
            return;
        }

        try
        {
            var image = await _host.CoverImages.GetAsync(new CoverRequest(track.Path, track.CoverKey,
                track.ModifiedAtUtc, track.FileSize), 512);
            if (image is not null)
            {
                row.GroupCoverImage = image;
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Group cover failed for {track.Path}: {ex.Message}");
        }
    }

    public async Task LoadRowCoverAsync(TrackRowViewModel row, bool force = false)
    {
        if (!force && row.CoverImage is not null)
        {
            return;
        }

        var track = row.Track;
        if (string.IsNullOrWhiteSpace(track.CoverKey))
        {
            return;
        }

        try
        {
            var image = await _host.CoverImages.GetAsync(new CoverRequest(track.Path, track.CoverKey,
                track.ModifiedAtUtc, track.FileSize), 128);
            if (image is not null)
            {
                row.CoverImage = image;
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Row cover failed for {track.Path}: {ex.Message}");
        }
    }

    private void OnPlayerStateChanged(object? sender, AudioPlaybackStateChangedEventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            switch (e.State)
            {
                case AudioPlaybackState.Playing:
                    IsPlaying = true;
                    break;
                case AudioPlaybackState.Paused:
                    IsPlaying = false;
                    break;
                case AudioPlaybackState.Stopped:
                    IsPlaying = false;
                    PositionSeconds = 0;
                    PositionText = "0:00";
                    if (!string.IsNullOrWhiteSpace(e.Error))
                    {
                        SetStatus($"播放出错：{e.Error}");
                    }

                    break;
            }

            OutputDescription = _host.Player.OutputDescription;
        }), DispatcherPriority.Background);
    }

    private void OnPlaybackEnded(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(new Action(async () =>
        {
            if (CurrentRow is null)
            {
                return;
            }

            await PlayNextAsync();
        }), DispatcherPriority.Background);
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_isSeeking || _isDisposed)
        {
            return;
        }

        var state = _host.Player.State;
        if (state is AudioPlaybackState.Playing or AudioPlaybackState.Paused)
        {
            var position = _host.Player.Position.TotalSeconds;
            var duration = _host.Player.Duration.TotalSeconds;
            if (duration > 0)
            {
                DurationSeconds = duration;
                DurationText = DisplayFormatter.Duration(_host.Player.Duration);
            }

            PositionSeconds = Math.Clamp(position, 0, Math.Max(duration, 0.1));
            PositionText = DisplayFormatter.Duration(_host.Player.Position);
        }
    }

    /// <summary>
    /// ADR-015：取消并（在波形形态下）重新开始当前曲目的波形加载；切歌 / 停止 / 切回滑块都会调用。
    /// </summary>
    public void StartWaveformLoad(string? path)
    {
        CancelWaveformLoad();
        if (!IsWaveformMode || string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        _ = LoadWaveformAsync(path, _waveformVersion, cts);
    }

    public void CancelWaveformLoad()
    {
        _waveformVersion++;
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        CurrentWaveform = null;
    }

    private async Task LoadWaveformAsync(string path, int version, CancellationTokenSource cts)
    {
        // Progress<T> 在 UI 线程构造，回调会自动回到 UI 线程；渐进快照只替换属性，
        // 控件自己负责追加几何。
        var progress = new Progress<WaveformPeaks>(peaks =>
        {
            if (version != _waveformVersion || cts.IsCancellationRequested)
            {
                return;
            }

            if (!string.Equals(CurrentRow?.Track.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentWaveform = peaks;
        });

        try
        {
            var watch = Stopwatch.StartNew();
            var peaks = await _host.Waveforms.GetAsync(path, progress, cts.Token);
            watch.Stop();
            if (peaks is null || version != _waveformVersion || cts.IsCancellationRequested)
            {
                return;
            }

            if (!string.Equals(CurrentRow?.Track.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentWaveform = peaks;
            _host.Logger.Debug(
                $"Waveform ready: columns={peaks.ColumnCount} valid={peaks.ValidCount} elapsed={watch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            // 切歌 / 停止是预期路径，不记日志。
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"Waveform load failed for {path}: {ex.Message}");
        }
    }

    /// <summary>ADR-015 波形拖动中的预览：只移动时间文本，不真正 Seek。</summary>
    public void PreviewSeek(double seconds)
    {
        if (!_isSeeking)
        {
            return;
        }

        var target = Math.Clamp(seconds, 0, Math.Max(DurationSeconds, 0));
        PositionSeconds = target;
        PositionText = DisplayFormatter.Duration(TimeSpan.FromSeconds(target));
    }

    public void BeginSeek()
    {
        _isSeeking = true;
        _host.Logger.Debug(
            $"Seek begin: playerPosition={_host.Player.Position.TotalSeconds:F3}s playerDuration={_host.Player.Duration.TotalSeconds:F3}s " +
            $"uiDuration={DurationSeconds:F3}s state={_host.Player.State}");
    }

    public void CancelSeek()
    {
        _isSeeking = false;
        _host.Logger.Debug(
            $"Seek cancelled: playerPosition={_host.Player.Position.TotalSeconds:F3}s state={_host.Player.State}");
        // Repaint the thumb from the real player position instead of the abandoned target.
        OnPositionTick(null, EventArgs.Empty);
    }

    public void CommitSeek(double seconds)
    {
        _isSeeking = false;
        var duration = DurationSeconds;
        var target = Math.Clamp(seconds, 0, Math.Max(duration, 0));
        _host.Logger.Debug(
            $"Seek commit: requested={seconds:F3}s target={target:F3}s duration={duration:F3}s " +
            $"playerPosition={_host.Player.Position.TotalSeconds:F3}s state={_host.Player.State}");

        _host.Player.Seek(TimeSpan.FromSeconds(target));
        PositionSeconds = target;
        PositionText = DisplayFormatter.Duration(TimeSpan.FromSeconds(target));

        _host.Logger.Debug(
            $"Seek applied: playerPosition={_host.Player.Position.TotalSeconds:F3}s state={_host.Player.State}");
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));

        if (_suppressVolumeSave)
        {
            return;
        }

        var normalized = Math.Clamp(value / 100.0, 0, 1);
        _host.Player.Volume = IsMuted ? 0 : normalized;
        Settings.Volume = normalized;
        QueueSettingsSave();
    }

    partial void OnIsMutedChanged(bool value)
    {
        _host.Player.Volume = value ? 0 : Math.Clamp(Volume / 100.0, 0, 1);
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(MuteToolTip));
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    partial void OnShowAlbumPanelChanged(bool value)
    {
        Settings.ShowAlbumPanel = value;
        QueueSettingsSave();
    }

    // =====================================================================
    // Audio output
    // =====================================================================
    public void RefreshDevices()
    {
        try
        {
            var devices = _host.Player.GetOutputDevices();
            _suppressOutputApply = true;
            try
            {
                Devices.Clear();
                foreach (var device in devices)
                {
                    Devices.Add(device);
                }

                SelectedDevice = Devices.FirstOrDefault(d =>
                                     string.Equals(d.Id, Settings.OutputDeviceId, StringComparison.OrdinalIgnoreCase))
                                 ?? Devices.FirstOrDefault(d => d.IsDefault)
                                 ?? Devices.FirstOrDefault();
                OutputModeIndex = Settings.OutputMode == OutputMode.Exclusive ? 1 : 0;
            }
            finally
            {
                _suppressOutputApply = false;
            }

            OutputDescription = ComposeOutputDescription();
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Refreshing audio devices failed", ex);
        }
    }

    private string ComposeOutputDescription() =>
        $"{SelectedDevice?.FriendlyName ?? "系统默认设备"} · {(OutputModeIndex == 1 ? "独占模式" : "共享模式")}";

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (_suppressOutputApply || value is null)
        {
            return;
        }

        ApplyOutput();
    }

    partial void OnOutputModeIndexChanged(int value)
    {
        if (_suppressOutputApply)
        {
            return;
        }

        ApplyOutput();
    }

    private void ApplyOutput()
    {
        var mode = OutputModeIndex == 1 ? OutputMode.Exclusive : OutputMode.Shared;
        Settings.OutputDeviceId = SelectedDevice?.Id;
        Settings.OutputMode = mode;
        _host.Player.ConfigureOutput(Settings.OutputDeviceId, mode);
        OutputDescription = _host.Player.OutputDescription;
        QueueSettingsSave();
        SetStatus($"输出已切换：{SelectedDevice?.FriendlyName ?? "系统默认"} · {(mode == OutputMode.Exclusive ? "独占" : "共享")}");
    }

    // =====================================================================
    // Library, scanning and file watching
    // =====================================================================
    [RelayCommand]
    private async Task AddLibraryFolderAsync()
    {
        var folder = _dialogs.PickFolder("选择要加入媒体库的文件夹");
        if (folder is null)
        {
            return;
        }

        try
        {
            var existingFolders = await _host.LibraryRepository.GetFoldersAsync();
            var overlapping = existingFolders.FirstOrDefault(f => PathUtil.FoldersOverlap(f.Path, folder));
            if (overlapping is not null &&
                !_dialogs.Confirm($"该文件夹与媒体库中已有的目录重叠：\n{overlapping.Path}\n\n重叠会导致同一批文件被重复扫描，仍要添加吗？"))
            {
                return;
            }

            var added = await _host.LibraryRepository.AddFolderAsync(folder);
            await ReloadSourcesAsync();
            var node = Sources.FirstOrDefault(s => s.Kind == SourceKind.Folder && s.Folder?.Id == added.Id);
            if (node is not null)
            {
                await SelectSourceAsync(node);
            }

            _host.Watcher.Stop();
            _host.Watcher.Start(await _host.LibraryRepository.GetFoldersAsync());
            await ScanFolderAsync(added);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Adding library folder failed", ex);
            _dialogs.Error($"添加文件夹失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        await ScanLibraryAsync(silent: false);
    }

    private async Task ScanLibraryAsync(bool silent, bool repair = false)
    {
        if (IsScanning)
        {
            return;
        }

        var folders = await _host.LibraryRepository.GetFoldersAsync();
        if (folders.Count == 0)
        {
            if (!silent)
            {
                SetStatus("媒体库还没有文件夹，请先添加");
            }

            return;
        }

        IsScanning = true;
        IsBusy = true;
        if (repair)
        {
            SetStatus("正在修复媒体库标签…");
        }

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;
        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanProgress = p.Percent;
                if (!string.IsNullOrEmpty(p.CurrentFile))
                {
                    SetStatus($"扫描中 {p.Processed}/{p.Total}：{System.IO.Path.GetFileName(p.CurrentFile)}");
                }
            });

            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                var result = await _host.Scanner.ScanFolderAsync(folder, progress, token);
                _host.Logger.Info($"Folder scan {folder.Path}: +{result.Added}/-{result.Removed} in {result.Elapsed.TotalSeconds:F1}s");
            }

            await ReloadSourcesAsync();
            await RefreshCurrentSourceAsync(preserveSelection: true);
            SetStatus(repair ? "媒体库标签已修复" : "媒体库扫描完成");
            if (repair)
            {
                _host.Logger.Info("Metadata repair scan completed.");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("扫描已取消");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Library scan failed", ex);
            SetStatus($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsScanning = false;
            IsBusy = false;
            ScanProgress = 0;
        }
    }

    private async Task ScanFolderAsync(LibraryFolder folder)
    {
        IsScanning = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgress = p.Percent;
            if (!string.IsNullOrEmpty(p.CurrentFile))
            {
                SetStatus($"导入中 {p.Processed}/{p.Total}");
            }
        });
        try
        {
            var result = await _host.Scanner.ScanFolderAsync(folder, progress, CancellationToken.None);
            await ReloadSourcesAsync();
            await RefreshCurrentSourceAsync(preserveSelection: true);
            SetStatus($"已导入 {result.Added} 首歌曲（{result.Elapsed.TotalSeconds:F1} 秒）");
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"Scanning {folder.Path} failed", ex);
            SetStatus($"扫描失败：{ex.Message}");
        }
        finally
        {
            IsScanning = false;
            ScanProgress = 0;
        }
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        await ScanLibraryAsync();
    }

    private void OnLibraryFileChanged(object? sender, LibraryTrackChangedEventArgs e)
    {
        lock (_pendingWatchPaths)
        {
            _pendingWatchPaths.Add(e.Path);
        }

        _dispatcher.BeginInvoke(new Action(() => _watcherTimer.Start()), DispatcherPriority.Background);
    }

    private void OnWatcherTick(object? sender, EventArgs e)
    {
        _watcherTimer.Stop();
        List<string> paths;
        lock (_pendingWatchPaths)
        {
            paths = _pendingWatchPaths.ToList();
            _pendingWatchPaths.Clear();
        }

        if (paths.Count == 0)
        {
            return;
        }

        _ = ProcessWatchedChangesAsync(paths);
    }

    private async Task ProcessWatchedChangesAsync(IReadOnlyList<string> paths)
    {
        try
        {
            var folders = await _host.LibraryRepository.GetFoldersAsync();
            var changed = false;
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    var folder = folders.FirstOrDefault(f => PathUtil.IsUnderFolder(path, f.Path));
                    if (folder is null)
                    {
                        continue;
                    }

                    await _host.Scanner.IndexFileAsync(path, inLibrary: true, folder.Id);
                    changed = true;
                }
                else
                {
                    await _host.LibraryRepository.MarkTrackMissingAsync(path);
                    changed = true;
                }
            }

            if (changed)
            {
                await ReloadSourcesAsync();
                await RefreshCurrentSourceAsync(preserveSelection: true);
                _host.Logger.Info($"Library synchronized after {paths.Count} file change(s).");
                SetStatus($"媒体库已同步：{paths.Count} 个文件变化");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Processing watched file changes failed", ex);
        }
    }

    public async Task RefreshCurrentSourceAsync(bool preserveSelection = false)
    {
        if (SelectedSource is null)
        {
            return;
        }

        var selectedPaths = preserveSelection
            ? Tracks.Where(t => IsRowSelected(t)).Select(t => t.Track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var source = SelectedSource;
        var tracks = await LoadTracksForSourceAsync(source);
        ApplyTracks(tracks, source);
        if (selectedPaths is { Count: > 0 })
        {
            foreach (var row in Tracks.Where(r => selectedPaths.Contains(r.Track.Path)))
            {
                row.IsSelected = true;
            }
        }
    }

    /// <summary>
    /// Re-indexes visible rows whose cached metadata no longer matches the file on disk.
    /// Files outside every library folder (for example files added directly to a playlist) are
    /// never visited by the library scanner or the file watcher, so their tags would otherwise
    /// stay stale forever. Runs in the background and is throttled because it is also triggered
    /// when the window is activated.
    /// </summary>
    public async Task RefreshStaleTracksAsync(bool force = false)
    {
        if (_refreshingTracks || _isDisposed || Tracks.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastTrackRefreshUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _refreshingTracks = true;
        _lastTrackRefreshUtc = now;
        try
        {
            var source = SelectedSource;
            var rows = Tracks.ToList();
            // FileInfo lookups and tag reads must not block the UI thread for large playlists.
            var changed = await Task.Run(async () =>
            {
                var count = 0;
                foreach (var row in rows)
                {
                    var track = row.Track;
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(track.Path);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!info.Exists || !TrackRefresh.NeedsRefresh(track, info.Length, info.LastWriteTimeUtc))
                    {
                        continue;
                    }

                    try
                    {
                        await _host.Scanner.IndexFileAsync(track.Path, track.IsInLibrary, track.FolderId);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        _host.Logger.Warn($"Refreshing track metadata failed: {track.Path} ({ex.Message})");
                    }
                }

                return count;
            });

            if (changed > 0 && IsSameSource(source, SelectedSource))
            {
                await RefreshCurrentSourceAsync(preserveSelection: true);
                _host.Logger.Info($"Refreshed metadata for {changed} track(s) from disk.");
                SetStatus($"已刷新 {changed} 个文件的标签信息");
            }
        }
        finally
        {
            _refreshingTracks = false;
        }
    }

    private static bool IsSameSource(SourceNodeViewModel? left, SourceNodeViewModel? right)
        => left is not null
           && right is not null
           && left.Kind == right.Kind
           && left.Playlist?.Id == right.Playlist?.Id
           && left.Folder?.Id == right.Folder?.Id;

    private bool IsRowSelected(TrackRowViewModel row) => SelectedRows.Contains(row);

    public HashSet<TrackRowViewModel> SelectedRows { get; } = [];

    // =====================================================================
    // Files, playlists and drag & drop
    // =====================================================================

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var files = _dialogs.PickAudioFiles();
        if (files.Count > 0)
        {
            await AddFilesToCurrentPlaylistAsync(files);
        }
    }

    public async Task AddFilesToCurrentPlaylistAsync(IEnumerable<string> paths, bool playFirst = false)
    {
        if (SelectedSource?.Kind == SourceKind.Playlist && SelectedSource.IsSmart)
        {
            SetStatus("智能播放列表不允许手动添加歌曲");
            return;
        }

        var files = paths.Where(PathUtil.IsSupportedAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            return;
        }

        SetStatus($"正在导入 {files.Count} 个文件…");
        var indexed = await IndexFilesAsync(files);

        if (SelectedSource?.Kind == SourceKind.Playlist && SelectedSource.Playlist is not null)
        {
            await _host.PlaylistRepository.AddPlaylistItemsAsync(SelectedSource.Playlist.Id, indexed);
            await RefreshCurrentSourceAsync();
        }
        else
        {
            // Dropping files while the library (or a folder) is displayed appends them
            // to the default playlist, which is the "current queue" in this player.
            var defaultPlaylist = await _host.PlaylistRepository.GetDefaultPlaylistAsync();
            if (defaultPlaylist is not null)
            {
                await _host.PlaylistRepository.AddPlaylistItemsAsync(defaultPlaylist.Id, indexed);
            }

            await RefreshCurrentSourceAsync(preserveSelection: true);
        }

        await ReloadSourcesAsync();
        SetStatus($"已导入 {indexed.Count} 首歌曲");

        if (playFirst && indexed.Count > 0)
        {
            var first = Tracks.FirstOrDefault(t =>
                string.Equals(t.Track.Path, indexed[0].Path, StringComparison.OrdinalIgnoreCase));
            if (first is not null)
            {
                await PlayRowAsync(first);
            }
        }
    }

    private async Task<IReadOnlyList<Track>> IndexFilesAsync(IReadOnlyList<string> files)
    {
        var folders = await _host.LibraryRepository.GetFoldersAsync();
        var libraryFiles = new List<string>();
        var externalFiles = new List<string>();
        foreach (var file in files)
        {
            var folder = folders.FirstOrDefault(f => PathUtil.IsUnderFolder(file, f.Path));
            if (folder is null)
            {
                externalFiles.Add(file);
            }
            else
            {
                libraryFiles.Add(file);
            }
        }

        var result = new List<Track>();
        if (libraryFiles.Count > 0)
        {
            result.AddRange(await _host.Scanner.IndexFilesAsync(libraryFiles, inLibrary: true));
        }

        if (externalFiles.Count > 0)
        {
            result.AddRange(await _host.Scanner.IndexFilesAsync(externalFiles, inLibrary: false));
        }

        return result;
    }

    public async Task HandleExternalFilesAsync(IReadOnlyList<string> paths)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = paths.Where(PathUtil.IsSupportedAudio).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            var defaultPlaylist = await _host.PlaylistRepository.GetDefaultPlaylistAsync();
            if (defaultPlaylist is null)
            {
                SetStatus("找不到默认播放列表");
                return;
            }

            var indexed = await IndexFilesAsync(files);
            var t1 = stopwatch.ElapsedMilliseconds;
            await _host.PlaylistRepository.ClearPlaylistAsync(defaultPlaylist.Id);
            await _host.PlaylistRepository.AddPlaylistItemsAsync(defaultPlaylist.Id, indexed);
            var t2 = stopwatch.ElapsedMilliseconds;
            await ReloadSourcesAsync();
            var t3 = stopwatch.ElapsedMilliseconds;

            var node = Sources.FirstOrDefault(s => s.Kind == SourceKind.Playlist && s.Playlist?.Id == defaultPlaylist.Id);
            if (node is not null)
            {
                await SelectSourceAsync(node);
                var t4 = stopwatch.ElapsedMilliseconds;
                var first = Tracks.FirstOrDefault();
                if (first is not null)
                {
                    await PlayRowAsync(first);
                }

                _host.Logger.Info($"External open phases: index={t1}, playlist={t2 - t1}, sources={t3 - t2}, " +
                                  $"select={t4 - t3}, play={stopwatch.ElapsedMilliseconds - t4} ms");
            }

            _host.Logger.Info($"External files handled in {stopwatch.ElapsedMilliseconds} ms ({indexed.Count} files).");
            SetStatus($"已用 {indexed.Count} 个文件替换默认播放列表");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Handling external files failed", ex);
            SetStatus($"打开文件失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task NewPlaylistAsync()
    {
        var name = _dialogs.AskText("新建播放列表", "播放列表名称", "新播放列表");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (PlaylistNameExists(name))
        {
            _dialogs.Info($"已经存在名为“{name}”的播放列表，请换一个名称。");
            return;
        }

        await _host.PlaylistRepository.CreatePlaylistAsync(name, PlaylistKind.Normal, null);
        await ReloadSourcesAsync();
        var node = Sources.LastOrDefault(s => s.Kind == SourceKind.Playlist && s.Playlist?.Name == name);
        if (node is not null)
        {
            await SelectSourceAsync(node);
        }

        SetStatus($"已创建播放列表：{name}");
    }

    private bool PlaylistNameExists(string name, long? exceptPlaylistId = null) =>
        Sources.Any(s => s.Kind == SourceKind.Playlist &&
                         s.Playlist is not null &&
                         s.Playlist.Id != exceptPlaylistId &&
                         string.Equals(s.Playlist.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task NewSmartPlaylistAsync()
    {
        var definition = _dialogs.EditSmartPlaylist("新建智能播放列表", new SmartPlaylistDefinition
        {
            Rules = [new SmartRule { Field = "artist", Operator = "contains", Value = string.Empty }]
        });
        if (definition is null)
        {
            return;
        }

        var name = _dialogs.AskText("智能播放列表名称", "名称", "智能播放列表");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (PlaylistNameExists(name))
        {
            _dialogs.Info($"已经存在名为“{name}”的播放列表，请换一个名称。");
            return;
        }

        await _host.PlaylistRepository.CreatePlaylistAsync(name, PlaylistKind.Smart, definition);
        await ReloadSourcesAsync();
        var node = Sources.LastOrDefault(s => s.Kind == SourceKind.Playlist && s.Playlist?.Name == name);
        if (node is not null)
        {
            await SelectSourceAsync(node);
        }

        SetStatus($"已创建智能播放列表：{name}");
    }

    [RelayCommand]
    private async Task EditSmartRulesAsync()
    {
        if (SelectedSource?.Playlist is not { IsSmart: true } playlist)
        {
            return;
        }

        var definition = _dialogs.EditSmartPlaylist(playlist.Name, playlist.GetSmartDefinition());
        if (definition is null)
        {
            return;
        }

        await _host.PlaylistRepository.UpdatePlaylistRulesAsync(playlist.Id, definition);
        await ReloadSourcesAsync();
        await RefreshCurrentSourceAsync(preserveSelection: true);
        SetStatus("智能播放列表条件已更新");
    }

    [RelayCommand]
    private async Task RenamePlaylistAsync()
    {
        if (SelectedSource?.IsRemovablePlaylist != true || SelectedSource.Playlist is null)
        {
            SetStatus("默认播放列表或媒体库不能重命名");
            return;
        }

        var name = _dialogs.AskText("重命名播放列表", "名称", SelectedSource.Playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name == SelectedSource.Playlist.Name)
        {
            return;
        }

        if (PlaylistNameExists(name, SelectedSource.Playlist.Id))
        {
            _dialogs.Info($"已经存在名为“{name}”的播放列表，请换一个名称。");
            return;
        }

        await _host.PlaylistRepository.RenamePlaylistAsync(SelectedSource.Playlist.Id, name);
        await ReloadSourcesAsync();
        SetStatus("播放列表已重命名");
    }

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        if (SelectedSource?.IsRemovablePlaylist != true || SelectedSource.Playlist is null)
        {
            SetStatus("默认播放列表不能删除");
            return;
        }

        if (!_dialogs.Confirm($"确定删除播放列表“{SelectedSource.Playlist.Name}”吗？文件不会被删除。"))
        {
            return;
        }

        await _host.PlaylistRepository.DeletePlaylistAsync(SelectedSource.Playlist.Id);
        await ReloadSourcesAsync();
        await SelectSourceAsync(Sources.FirstOrDefault());
        SetStatus("播放列表已删除");
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedSource?.Kind != SourceKind.Playlist || SelectedSource.Playlist is null)
        {
            SetStatus("只能在播放列表中移除歌曲（媒体库请通过移除文件夹或文件管理）");
            return;
        }

        if (SelectedSource.IsSmart)
        {
            SetStatus("智能播放列表不允许手动移除歌曲");
            return;
        }

        var items = Tracks.Where(t => SelectedRows.Contains(t)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(i => i.Track.PlaylistItemId).Where(id => id > 0).ToList();
        if (ids.Count > 0)
        {
            await _host.PlaylistRepository.RemovePlaylistItemsAsync(ids);
        }

        await RefreshCurrentSourceAsync();
        await ReloadSourcesAsync();
        SetStatus($"已从播放列表移除 {items.Count} 首歌曲");
    }

    [RelayCommand]
    private async Task AddSelectedToPlaylistAsync()
    {
        var playlists = (await _host.PlaylistRepository.GetPlaylistsAsync())
            .Where(p => !p.IsSmart && p.Id != SelectedSource?.Playlist?.Id)
            .ToList();
        if (playlists.Count == 0)
        {
            _dialogs.Info("没有其他普通播放列表，请先新建一个。");
            return;
        }

        var playlistId = _dialogs.PickPlaylist(playlists, "添加到播放列表");
        if (playlistId is null)
        {
            return;
        }

        var selected = Tracks.Where(IsRowSelected).ToList();
        if (selected.Count == 0 && SelectedRow is not null)
        {
            selected.Add(SelectedRow);
        }

        if (selected.Count == 0)
        {
            return;
        }

        await _host.PlaylistRepository.AddPlaylistItemsAsync(playlistId.Value, selected.Select(t => t.Track).ToList());
        await ReloadSourcesAsync();
        SetStatus($"已添加 {selected.Count} 首歌曲到播放列表");
    }

    [RelayCommand]
    private async Task EditTagsAsync(TrackRowViewModel? row)
    {
        var rows = Tracks.Where(IsRowSelected).ToList();
        if (rows.Count <= 1)
        {
            row ??= SelectedRow;
            rows = row is null ? [] : [row];
        }

        if (rows.Count == 0)
        {
            return;
        }

        var tracks = rows.Select(r => r.Track).ToList();
        try
        {
            var tags = await Task.Run(() => tracks.Select(track => _host.TagService.Read(track.Path)).ToList());
            var edit = _dialogs.EditTags(tracks, tags);
            if (edit is null)
            {
                return;
            }

            var saved = 0;
            var failures = new List<string>();
            for (var i = 0; i < rows.Count; i++)
                {
                    var target = rows[i];
                    try
                    {
                        var model = TagBatchEditor.CreateEdit(tags[i], edit);
                        var oldCover = target.Track.CoverKey;
                        await Task.Run(() => _host.TagService.Write(target.Track.Path, model));
                        var updated = await _host.Scanner.IndexFileAsync(target.Track.Path, target.Track.IsInLibrary, target.Track.FolderId);
                        if (updated is not null)
                        {
                            target.UpdateTrack(updated);
                            _host.Covers?.Invalidate(oldCover);
                            _host.Covers?.Invalidate(updated.CoverKey);
                            _host.CoverImages.Invalidate(oldCover);
                            _host.CoverImages.Invalidate(updated.CoverKey);
                            await LoadRowCoverAsync(target, force: true);
                            if (ReferenceEquals(CurrentRow, target))
                            {
                                UpdateNowPlayingMetadata(target);
                                BeginNowPlayingCoverLoad(updated);
                            }
                        }

                        saved++;
                    }
                    catch (Exception ex)
                    {
                        _host.Logger.Error($"Tag edit failed for {target.Track.Path}", ex);
                        failures.Add($"{System.IO.Path.GetFileName(target.Track.Path)}：{ex.Message}");
                    }
                }

            await ReloadSourcesAsync();
            if (failures.Count > 0)
            {
                _dialogs.Error($"标签保存完成：成功 {saved} 个，失败 {failures.Count} 个。\n" + string.Join("\n", failures.Take(5)));
                SetStatus($"标签保存：成功 {saved} 个，失败 {failures.Count} 个");
            }
            else
            {
                _host.Logger.Info($"Tags saved: {saved} file(s)");
                SetStatus(saved > 1 ? $"已保存 {saved} 个文件的标签" : "标签已保存");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Tag edit failed", ex);
            _dialogs.Error($"保存标签失败：{ex.Message}");
        }
    }

    // =====================================================================
    // 文件操作（ADR-021）：复制到 / 移动到 / 删除到回收站
    // =====================================================================

    /// <summary>「文件操作」菜单作用到的行：优先多选，否则回退到当前行。</summary>
    public IReadOnlyList<TrackRowViewModel> GetFileOperationTargets()
    {
        var rows = Tracks.Where(IsRowSelected).ToList();
        if (rows.Count == 0 && SelectedRow is not null)
        {
            rows.Add(SelectedRow);
        }

        return rows;
    }

    /// <summary>记住「复制到 / 移动到」的目标文件夹（去重、限量，见 <see cref="FileDestinations"/>）。</summary>
    public void RememberFileDestination(string directory)
    {
        if (!FileDestinations.Add(Settings.FileDestinations, directory))
        {
            return;
        }

        QueueSettingsSave();
        SetStatus($"已记住目标文件夹：{directory}");
    }

    public Task CopySelectedFilesAsync(string? destinationDirectory = null) =>
        TransferSelectedFilesAsync(move: false, destinationDirectory);

    public Task MoveSelectedFilesAsync(string? destinationDirectory = null) =>
        TransferSelectedFilesAsync(move: true, destinationDirectory);

    /// <summary>把选中的文件移到回收站（可还原），并同步媒体库/播放列表状态。</summary>
    public async Task DeleteSelectedFilesAsync()
    {
        var rows = GetFileOperationTargets();
        if (rows.Count == 0)
        {
            SetStatus("请先在列表中选择歌曲");
            return;
        }

        var paths = rows
            .Select(r => r.Track.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var message = paths.Count == 1
            ? $"将文件“{System.IO.Path.GetFileName(paths[0])}”移到回收站？\n\n可以随时从回收站还原。"
            : $"将 {paths.Count} 个文件移到回收站？\n\n可以随时从回收站还原。";
        if (!_dialogs.Confirm(message, "删除文件"))
        {
            return;
        }

        SetStatus($"正在把 {paths.Count} 个文件移到回收站…");
        try
        {
            var result = await _host.Files.DeleteToRecycleBinAsync(paths);
            var deleted = result.Items.Where(i => i.Succeeded).Select(i => i.SourcePath).ToList();
            if (deleted.Count > 0)
            {
                await _host.LibraryRepository.MarkTracksMissingAsync(deleted);
                foreach (var path in deleted)
                {
                    _host.Waveforms.Invalidate(path);
                }

                StopPlaybackIfAffected(deleted);
                await ReloadSourcesAsync();
                await RefreshCurrentSourceAsync(preserveSelection: true);
            }

            if (result.Failed == 0)
            {
                _host.Logger.Info($"Files moved to recycle bin: {deleted.Count} file(s)");
                SetStatus($"已把 {deleted.Count} 个文件移到回收站");
            }
            else
            {
                var details = string.Join("\n", result.Items.Where(i => !i.Succeeded)
                    .Take(5)
                    .Select(i => $"{System.IO.Path.GetFileName(i.SourcePath)}：{i.Error}"));
                _host.Logger.Warn($"Recycle bin delete failed: {result.Failed} of {paths.Count} file(s)");
                _dialogs.Error($"删除完成：成功 {deleted.Count} 个，失败 {result.Failed} 个。\n{details}");
                SetStatus($"删除到回收站：成功 {deleted.Count} 个，失败 {result.Failed} 个");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Delete to recycle bin failed", ex);
            _dialogs.Error($"删除文件失败：{ex.Message}");
            SetStatus("删除文件失败");
        }
    }

    private async Task TransferSelectedFilesAsync(bool move, string? destinationDirectory)
    {
        var rows = GetFileOperationTargets();
        if (rows.Count == 0)
        {
            SetStatus("请先在列表中选择歌曲");
            return;
        }

        var paths = rows
            .Select(r => r.Track.Path)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (paths.Count == 0)
        {
            return;
        }

        var destination = destinationDirectory;
        if (string.IsNullOrWhiteSpace(destination))
        {
            destination = _dialogs.PickFolder(move ? "选择移动到的文件夹" : "选择复制到的文件夹");
            if (string.IsNullOrWhiteSpace(destination))
            {
                return;
            }

            RememberFileDestination(destination);
        }

        var verb = move ? "移动" : "复制";
        if (move)
        {
            var message = paths.Count == 1
                ? $"将文件“{System.IO.Path.GetFileName(paths[0])}”移动到：\n{destination}\n\n原位置的文件会被移除。"
                : $"将 {paths.Count} 个文件移动到：\n{destination}\n\n原位置的文件会被移除。";
            if (!_dialogs.Confirm(message, "移动文件"))
            {
                return;
            }
        }

        SetStatus($"正在{verb} {paths.Count} 个文件…");
        try
        {
            var result = move
                ? await _host.Files.MoveAsync(paths, destination)
                : await _host.Files.CopyAsync(paths, destination);
            await ApplyFileTransferResultAsync(move, result);

            if (result.Failed == 0)
            {
                SetStatus($"已{verb} {result.Succeeded} 个文件到 {destination}");
            }
            else
            {
                var details = string.Join("\n", result.Items.Where(i => !i.Succeeded)
                    .Take(5)
                    .Select(i => $"{System.IO.Path.GetFileName(i.SourcePath)}：{i.Error}"));
                _host.Logger.Warn($"File transfer failed: {result.Failed} of {paths.Count} file(s)");
                _dialogs.Error($"{verb}完成：成功 {result.Succeeded} 个，失败 {result.Failed} 个。\n{details}");
                SetStatus($"{verb}：成功 {result.Succeeded} 个，失败 {result.Failed} 个");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"File transfer failed (move={move})", ex);
            _dialogs.Error($"{verb}文件失败：{ex.Message}");
            SetStatus($"{verb}文件失败");
        }
    }

    /// <summary>文件已成功复制/移动后，同步数据库与当前列表（复制入库、移动改路径）。</summary>
    private async Task ApplyFileTransferResultAsync(bool move, FileOperationResult result)
    {
        var succeeded = result.Items
            .Where(i => i.Succeeded && !string.IsNullOrWhiteSpace(i.TargetPath))
            .ToList();
        if (succeeded.Count == 0)
        {
            return;
        }

        var folders = await _host.LibraryRepository.GetFoldersAsync();
        var changedPaths = new List<string>(succeeded.Count);
        foreach (var item in succeeded)
        {
            var target = item.TargetPath!;
            changedPaths.Add(item.SourcePath);
            var folder = folders.FirstOrDefault(f => PathUtil.IsUnderFolder(target, f.Path));
            if (move)
            {
                await _host.LibraryRepository.UpdateTrackPathAsync(item.SourcePath, target, folder?.Id);
                _host.Waveforms.Invalidate(item.SourcePath);
            }
            else if (folder is not null)
            {
                // 复制到媒体库文件夹内的文件应当立即被索引；复制到库外则只留文件。
                await _host.Scanner.IndexFileAsync(target, inLibrary: true, folder.Id);
            }
        }

        if (move)
        {
            StopPlaybackIfAffected(changedPaths);
        }

        await ReloadSourcesAsync();
        await RefreshCurrentSourceAsync(preserveSelection: true);
    }

    /// <summary>正在播放的文件被移动/删除后停止播放（否则当前行与磁盘上的文件会对不上）。</summary>
    private void StopPlaybackIfAffected(IReadOnlyCollection<string> affectedPaths)
    {
        var playing = _host.Player.CurrentTrackPath;
        if (playing is null || !affectedPaths.Contains(playing, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Stop();
        SetStatus("正在播放的文件已被移动或删除，播放已停止");
    }

    [RelayCommand]
    private void RevealInExplorer(TrackRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Track.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开资源管理器：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _host.Paths.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开日志目录：{ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _host.Paths.ConfigDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开配置目录：{ex.Message}");
        }
    }

    // =====================================================================
    // Settings / configuration
    // =====================================================================
    [RelayCommand]
    private async Task ExportConfigurationAsync()
    {
        var path = _dialogs.PickSaveFile("EchoGarage 配置 (*.json)|*.json", "echogarage-settings.json");
        if (path is null)
        {
            return;
        }

        try
        {
            PersistCurrentLayout();
            await _host.Settings.ExportAsync(path);
            SetStatus($"配置已导出：{path}");
        }
        catch (Exception ex)
        {
            _dialogs.Error($"导出失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportConfigurationAsync()
    {
        var path = _dialogs.PickOpenFile("EchoGarage 配置 (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        if (!_dialogs.Confirm("导入配置会覆盖当前的播放器设置（不会影响媒体库和播放列表）。继续吗？"))
        {
            return;
        }

        try
        {
            await _host.Settings.ImportAsync(path);
            Settings.ColumnPresets = _host.Settings.Current.ColumnPresets;
            Presets.Clear();
            foreach (var preset in _host.Settings.Current.ColumnPresets)
            {
                Presets.Add(preset);
            }

            CurrentLayout = SelectedSource?.Kind == SourceKind.Playlist && SelectedSource.Playlist is not null
                ? ViewLayoutFactory.Deserialize(SelectedSource.Playlist.ViewLayoutJson)
                : ViewLayoutFactory.Normalize(_host.Settings.Current.LibraryLayout.Clone());
            ShowAlbumPanel = _host.Settings.Current.ShowAlbumPanel;
            _suppressOutputApply = true;
            try
            {
                OutputModeIndex = _host.Settings.Current.OutputMode == OutputMode.Exclusive ? 1 : 0;
            }
            finally
            {
                _suppressOutputApply = false;
            }

            _suppressThemeApply = true;
            ThemeIndex = _host.Settings.Current.Theme == ThemeKind.Light ? 0 : 1;
            _suppressThemeApply = false;
            ThemeManager.Apply(_host.Settings.Current.Theme);

            ApplyOutput();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
            SortChanged?.Invoke(this, EventArgs.Empty);
            SetStatus("配置已导入");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Importing configuration failed", ex);
            _dialogs.Error($"导入失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void RegisterDefaultPlayer()
    {
        // Registration is opt-in and per-extension; nothing is registered at startup.
        _dialogs.ShowFileAssociations(_host);
        SetStatus("文件关联注册需要手动确认");
    }

    [RelayCommand]
    private void ToggleAlbumPanel() => ShowAlbumPanel = !ShowAlbumPanel;

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    // =====================================================================
    // Status helpers
    // =====================================================================
    public void SetStatus(string message)
    {
        StatusMessage = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        StatusMessage = IsScanning ? StatusMessage : "就绪";
    }

    private void QueueSettingsSave()
    {
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private void OnSettingsTimerTick(object? sender, EventArgs e)
    {
        _settingsTimer.Stop();
        _ = _host.Settings.SaveAsync();
    }

    public async Task ShutdownAsync()
    {
        _positionTimer.Stop();
        _statusTimer.Stop();
        _watcherTimer.Stop();
        _settingsTimer.Stop();
        PersistCurrentLayout();
        Settings.Volume = Math.Clamp(Volume / 100.0, 0, 1);
        Settings.OutputMode = OutputModeIndex == 1 ? OutputMode.Exclusive : OutputMode.Shared;
        Settings.OutputDeviceId = SelectedDevice?.Id;
        Settings.ShowAlbumPanel = ShowAlbumPanel;
        await _host.Settings.SaveAsync();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _host.Player.StateChanged -= OnPlayerStateChanged;
        _host.Player.PlaybackEnded -= OnPlaybackEnded;
        _host.Watcher.Changed -= OnLibraryFileChanged;
        _positionTimer.Stop();
        _statusTimer.Stop();
        _watcherTimer.Stop();
        _settingsTimer.Stop();
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        _selectionGate.Dispose();
    }
}
