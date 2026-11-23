using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using System.Windows.Media;
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
    // 偏好输出设备缺失时只提示一次回退，避免每次刷新重复打扰。
    private bool _fallbackNoticeShown;
    private bool _suppressVolumeSave;
    private bool _suppressVolumeCeilingSave;
    /// <summary>当前音量记忆上下文（设备端点 ID + 模式，ADR-033）；变化时才从记忆加载显示音量。</summary>
    private string? _volumeContextKey;
    private bool _suppressThemeApply;
    private bool _isSeeking;
    private bool _isDisposed;
    private bool _refreshingTracks;
    private readonly bool _restoreSessionOnStartup;
    private DateTime _lastTrackRefreshUtc = DateTime.MinValue;
    private int _consecutiveFailures;
    private List<TrackRowViewModel>? _shuffleOrder;

    /// <summary>
    /// True while <see cref="Tracks"/> itself is in the requested display order because a source
    /// switch pre-sorted the rows on a background thread. The views must not sort again in that
    /// case (see <see cref="TrackRowSorter"/>).
    /// </summary>
    private bool _viewUsesCollectionOrder;

    // Bumped whenever Tracks is replaced wholesale. The grouped-rebuild signature includes it so a
    // source switch always rebuilds the row cells even when the layout itself did not change.
    private int _tracksVersion;

    /// <summary>Signature of every input <see cref="RebuildGroupedView"/> derives from.</summary>
    private string? _groupedRebuildSignature;

    /// <summary>Shared secondary-format table; rows keep their texts valid while the reference is stable.</summary>
    private IReadOnlyDictionary<string, string?> _secondaryFormats = TrackRowViewModel.EmptySecondaryFormats;
    private string _secondaryFormatsSignature = string.Empty;

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

    /// <summary>Subtitle for the current source without any search filter applied.</summary>
    private string _basePlaylistSubtitle = string.Empty;

    /// <summary>Field restrictions from the advanced search dialog; null while using the inline search.</summary>
    private IReadOnlyList<SearchField>? _advancedFields;
    private SearchFieldMode _advancedMode = SearchFieldMode.Any;

    /// <summary>True while the advanced search dialog writes the keyword into <see cref="FilterText"/>.</summary>
    private bool _applyingAdvancedSearch;

    public MainViewModel(AppHost host, IDialogService dialogs, bool waveformModeDiagnostics = false,
        bool restoreSessionOnStartup = true)
    {
        _host = host;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _restoreSessionOnStartup = restoreSessionOnStartup;
        Settings = host.Settings.Current;
        _playMode = Settings.PlaybackMode;
        // 直接赋字段：初始化时不应触发切换回调（否则会写设置并尝试加载空路径）。
        // 诊断开关（--waveform）只覆盖本次进程，因此不改写 Settings.SeekBarMode。
        _isWaveformMode = waveformModeDiagnostics || Settings.SeekBarMode == SeekBarMode.Waveform;

        Sources = [];
        _sourcesView = CollectionViewSource.GetDefaultView(Sources);
        _sourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceNodeViewModel.Section)));

        Tracks = new BulkObservableCollection<TrackRowViewModel>();
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
        _host.DeviceMonitor.DevicesChanged += OnAudioDevicesChanged;

        ShowAlbumPanel = Settings.ShowAlbumPanel;
        // 音量上限按模式计算：先把模式下拉同步到设置，再做音量初始化，
        // 否则启动瞬间 CurrentOutputMode 还是默认的共享模式，会按共享上限算一次错误增益。
        _suppressOutputApply = true;
        try
        {
            OutputModeIndex = Settings.OutputMode == OutputMode.Exclusive ? 1 : 0;
        }
        finally
        {
            _suppressOutputApply = false;
        }

        InitializeVolumeState();
        _suppressThemeApply = true;
        ThemeIndex = (int)Settings.Theme;
        _suppressThemeApply = false;
        OutputDescription = _host.Player.OutputDescription;
        StatusMessage = "就绪";
    }

    public AppSettings Settings { get; }
    public ObservableCollection<SourceNodeViewModel> Sources { get; }
    public ICollectionView SourcesView => _sourcesView;
    public BulkObservableCollection<TrackRowViewModel> Tracks { get; }
    public ICollectionView TracksView { get; }
    public ICollectionView GroupedTracksView { get; }
    /// <summary>Header cells for the left-anchored part of the grouped list.</summary>
    public ObservableCollection<GroupedColumnHeaderViewModel> GroupedLeftColumns { get; } = [];
    /// <summary>Header cells pinned to the right edge of the grouped list.</summary>
    public ObservableCollection<GroupedColumnHeaderViewModel> GroupedRightColumns { get; } = [];
    public ObservableCollection<AudioDeviceInfo> Devices { get; }
    public ObservableCollection<ColumnPreset> Presets { get; }

    public IReadOnlyList<string> OutputModeOptions { get; } = ["共享模式", "独占模式"];
    public IReadOnlyList<string> ThemeOptions { get; } = ThemeCatalog.All.Select(theme => theme.DisplayName).ToArray();

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

        var theme = ThemeCatalog.ByIndex(value);
        Settings.Theme = theme.Kind;
        ThemeManager.Apply(theme.Kind);
        _host.Logger.Info($"Theme switched to {theme.Kind} ({theme.DisplayName})");
        QueueSettingsSave();
        SetStatus($"界面主题：{theme.DisplayName}");
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
    [NotifyPropertyChangedFor(nameof(ShowCoverLoadingSpinner))]
    private bool _isNowPlayingCoverLoading;

    /// <summary>Shows the note placeholder when a track is playing but no artwork exists.</summary>
    public bool ShowCoverPlaceholder =>
        HasCurrentTrack && !IsNowPlayingCoverLoading && CurrentCover is null;

    /// <summary>
    /// 只在“加载中且没有旧封面可显示”时转圈。切歌时旧封面会保留到新图解码完成并交叉淡入，
    /// 若在旧封面上叠一个转圈反而更跳，因此有旧图时不显示。
    /// </summary>
    public bool ShowCoverLoadingSpinner => IsNowPlayingCoverLoading && CurrentCover is null;

    partial void OnCurrentCoverChanged(System.Windows.Media.Imaging.BitmapSource? value)
    {
        OnPropertyChanged(nameof(ShowCoverPlaceholder));
        OnPropertyChanged(nameof(ShowCoverLoadingSpinner));
    }

    // =====================================================================
    // 正在播放页：封面柔光 + 标签卡片
    // =====================================================================

    private const string EmptyTagText = "—";

    /// <summary>「正在播放」页背景柔光的主色（自当前封面实时取色）。</summary>
    [ObservableProperty]
    private Color _nowPlayingGlowPrimary = Colors.Transparent;

    /// <summary>「正在播放」页背景柔光的次色（自当前封面实时取色）。</summary>
    [ObservableProperty]
    private Color _nowPlayingGlowSecondary = Colors.Transparent;

    [ObservableProperty]
    private string _nowPlayingAlbumArtist = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingComposer = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingGenre = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingYear = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingTrackNumber = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingDiscNumber = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingCodec = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingComment = EmptyTagText;

    [ObservableProperty]
    private string _nowPlayingPath = string.Empty;

    /// <summary>封面下方的格式徽标：编码器 · 码率 / 采样率。</summary>
    [ObservableProperty]
    private string _nowPlayingFormatText = EmptyTagText;

    /// <summary>
    /// 「正在播放」页徽标里的当前输出格式（共享/独占 · 输出采样率 · 位深 · 声道）。
    /// 来自播放器写入 WASAPI 的实际格式，而不是歌曲文件本身的格式。
    /// </summary>
    [ObservableProperty]
    private string _nowPlayingOutputText = "未建立输出";

    /// <summary>输出格式徽标的悬停提示：设备名，以及是否发生重采样 / 声道转换。</summary>
    [ObservableProperty]
    private string _nowPlayingOutputTooltip = string.Empty;

    [ObservableProperty]
    private string _playlistTitle = "媒体库";

    [ObservableProperty]
    private string _playlistSubtitle = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayGlyph))]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// True while a source switch is loading (source query, row build, view rebuild). Drives the
    /// loading overlay; it is reference-counted so queued switches keep the overlay up instead of
    /// hiding and re-showing it (which would flash on fast consecutive selections).
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

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

    /// <summary>
    /// True 表示波形还在读取缓存 / 分析中，且还没有任何可画的快照；波形进度条据此
    /// 显示占位加载动画（ADR-015）。第一个渐进快照到达后立即置 false。
    /// </summary>
    [ObservableProperty]
    private bool _isWaveformLoading;

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

    /// <summary>
    /// 音量滑块的悬停提示：显示当前模式下 100% 对应的上限（ADR-033）。
    /// </summary>
    public string VolumeToolTip =>
        $"音量 {Math.Round(Volume)}% · {CurrentModeText}音量上限 {Math.Round(CurrentVolumeCeiling * 100)}%";

    /// <summary>共享模式的音量上限（百分比 5–100）：音量滑块的 100% 映射到此值。ADR-033。</summary>
    [ObservableProperty]
    private double _sharedVolumeCeiling = 100;

    /// <summary>独占模式的音量上限（百分比 5–100）：避免绕过系统音量后输出过高功率。ADR-033。</summary>
    [ObservableProperty]
    private double _exclusiveVolumeCeiling = 100;

    private OutputMode CurrentOutputMode => OutputModeIndex == 1 ? OutputMode.Exclusive : OutputMode.Shared;

    private string CurrentModeText => CurrentOutputMode == OutputMode.Exclusive ? "独占模式" : "共享模式";

    private double CurrentVolumeCeiling => OutputVolumeResolver.GetCeiling(Settings, CurrentOutputMode);

    /// <summary>Sequential, random or single-track repeat playback; persisted in settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeLabel))]
    [NotifyPropertyChangedFor(nameof(PlayModeToolTip))]
    [NotifyPropertyChangedFor(nameof(IsShuffleEnabled))]
    [NotifyPropertyChangedFor(nameof(IsRepeatOneEnabled))]
    private PlaybackMode _playMode = PlaybackMode.Sequential;

    public bool IsShuffleEnabled => PlayMode == PlaybackMode.Shuffle;

    public bool IsRepeatOneEnabled => PlayMode == PlaybackMode.RepeatOne;

    public string PlayModeLabel => PlayMode switch
    {
        PlaybackMode.Shuffle => "随机",
        PlaybackMode.RepeatOne => "单曲循环",
        _ => "顺序"
    };

    public string PlayModeToolTip => PlayMode switch
    {
        PlaybackMode.Shuffle => "随机播放（点击切换为单曲循环）",
        PlaybackMode.RepeatOne => "单曲循环（点击切换为顺序播放）",
        _ => "顺序播放（点击切换为随机播放）"
    };

    partial void OnPlayModeChanged(PlaybackMode value)
    {
        // A new mode means the previous shuffled round is meaningless. The next "next" rebuilds it.
        _shuffleOrder = null;
        Settings.PlaybackMode = value;
        QueueSettingsSave();
        SetStatus(value switch
        {
            PlaybackMode.Shuffle => "已切换为随机播放",
            PlaybackMode.RepeatOne => "已切换为单曲循环",
            _ => "已切换为顺序播放"
        });
    }

    [RelayCommand]
    private void TogglePlayMode() =>
        PlayMode = PlayMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.RepeatOne,
            _ => PlaybackMode.Sequential
        };

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

    // =====================================================================
    // 顶部 TAB：播放列表页 / 正在播放页
    // =====================================================================

    /// <summary>true 时显示「正在播放」页；false 显示原来的播放列表页。本次运行内记忆，不写设置。</summary>
    [ObservableProperty]
    private bool _isNowPlayingPageActive;

    public bool IsPlaylistPageActive => !IsNowPlayingPageActive;

    /// <summary>播放列表页右侧的专辑面板；「正在播放」页有自己整页的右侧列表，专辑面板要收起。</summary>
    public bool ShowPlaylistAlbumPanel => ShowAlbumPanel && !IsNowPlayingPageActive;

    partial void OnIsNowPlayingPageActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPlaylistPageActive));
        OnPropertyChanged(nameof(ShowPlaylistAlbumPanel));
        if (value)
        {
            RebuildNowPlayingPlaylist();
        }
    }

    /// <summary>
    /// 「正在播放」页右侧列表的数据快照：与播放列表页当前可见顺序（含搜索过滤、不含专辑分组）一致。
    /// 行对象与播放列表页共享，因此播放指示、封面会随现有绑定自动更新。
    /// </summary>
    public IReadOnlyList<TrackRowViewModel> NowPlayingPlaylistRows
    {
        get => _nowPlayingPlaylistRows;
        private set
        {
            if (ReferenceEquals(_nowPlayingPlaylistRows, value))
            {
                return;
            }

            _nowPlayingPlaylistRows = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NowPlayingPlaylistCountText));
        }
    }

    private IReadOnlyList<TrackRowViewModel> _nowPlayingPlaylistRows = [];

    /// <summary>「正在播放」页右侧列表头部的数量标签。</summary>
    public string NowPlayingPlaylistCountText => $"{NowPlayingPlaylistRows.Count:N0} 首";

    /// <summary>
    /// 按播放列表页当前可见顺序重建「正在播放」页的右侧列表。分组视图开启时使用分组视图的
    /// 枚举顺序（分组不改变行顺序，只改变呈现），关闭时使用扁平视图顺序。
    /// </summary>
    public void RebuildNowPlayingPlaylist()
    {
        if (!IsNowPlayingPageActive)
        {
            return;
        }

        var view = IsGroupedViewActive ? (ICollectionView)_groupedTracksView : _tracksView;
        NowPlayingPlaylistRows = view.Cast<TrackRowViewModel>().ToList();
    }

    [RelayCommand]
    private void ShowPlaylistPage() => IsNowPlayingPageActive = false;

    [RelayCommand]
    private void ShowNowPlayingPage() => IsNowPlayingPageActive = true;


    /// <summary>列表行封面模板里的显示边长（DIP），与 MainWindow.xaml 的 Cell.Cover 保持一致。</summary>
    private const double RowCoverDisplaySize = 24;

    /// <summary>
    /// 同时挂在界面上的相册组封面数量上限。每组封面约 (96 DIP × DPI)² × 4 字节，
    /// 视口附近一般 10–30 组；超出部分立即摘掉，让内存只跟“能看到的内容”有关，
    /// 而不是跟“滚过多少组”有关（2026-11-21：1604 组各留一张 512px 图 = 1.6 GB）。
    /// </summary>
    private const int VisibleGroupCoverLimit = 512;

    /// <summary>回收时一次裁到的目标数量，避免“每滚动一格就重建一次布局”。</summary>
    private const int VisibleGroupCoverTrimTarget = 384;

    private readonly LinkedList<TrackRowViewModel> _visibleGroupCovers = new();
    private readonly Dictionary<TrackRowViewModel, LinkedListNode<TrackRowViewModel>> _visibleGroupCoverNodes = new();
    private readonly object _visibleGroupCoverGate = new();
    private System.Windows.Threading.DispatcherTimer? _groupCoverTrimTimer;

    private double _dpiScale = 1.0;

    /// <summary>
    /// 列表所在窗口的 DPI 比例，由主窗口在 Loaded / DpiChanged 时写入。封面按“显示尺寸 × DPI”
    /// 解码，避免 4K/150% 下把 96 DIP 的相册封面解码成 512 px（约 12 倍的像素浪费）。
    /// </summary>
    public double DpiScale
    {
        get => _dpiScale;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 4.0);
            if (Math.Abs(clamped - _dpiScale) < 0.001)
            {
                return;
            }

            _dpiScale = clamped;
        }
    }

    /// <summary>列表行封面的解码宽度（像素）。行封面模板只有 24 DIP 见方。</summary>
    public int RowCoverDecodeWidth =>
        Math.Clamp((int)Math.Ceiling(RowCoverDisplaySize * DpiScale), 32, 256);

    /// <summary>相册组封面的解码宽度（像素），跟随 <see cref="GroupCoverSize"/> 与 DPI。</summary>
    public int GroupCoverDecodeWidth =>
        Math.Clamp((int)Math.Ceiling(GroupCoverSize * DpiScale), 64, 640);

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
        RebuildNowPlayingPlaylist();
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
    /// Raised once at startup when the last played track was selected without playback,
    /// so the window can scroll that row into view after the layout is applied.
    /// </summary>
    public event EventHandler<TrackRowViewModel>? SelectionRestored;

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
        OnPropertyChanged(nameof(HasCurrentTrack));
        OnPropertyChanged(nameof(ShowCoverPlaceholder));
        OnPropertyChanged(nameof(NowPlayingStatusText));
    }

    // =====================================================================
    // 播放可视化（ADR-023）
    // =====================================================================

    /// <summary>右栏频谱可视化的数据源（由 AppHost 持有，生命周期与应用一致）。</summary>
    public ISpectrumSource Spectrum => _host.Spectrum;

    /// <summary>渲染层使用的不可变参数快照；设置变更时整体替换以触发视图重建。</summary>
    [ObservableProperty]
    private SpectrumOptions? _spectrumOptions;

    /// <summary>右栏是否显示可视化（设置里关掉时整块隐藏，把空间还给窗口）。</summary>
    public bool ShowVisualizer => Settings.Visualization.Kind != VisualizerKind.None;

    /// <summary>右栏可视化的渲染帧率上限（FPS，0 = 不限制；ADR-023（A1 修订），由设置页即时生效）。</summary>
    public int VisualizerFrameRateLimit => Settings.Visualization.FrameRateLimit.ToFps();

    /// <summary>
    /// 把设置里的可视化参数推给分析引擎，并刷新视图用的快照。
    /// 启动、设置窗改参数、导入配置后调用。
    /// </summary>
    public void RefreshVisualizer()
    {
        var visualization = Settings.Visualization;
        _host.Spectrum.Apply(visualization);
        SpectrumOptions = EchoGarage.Core.Models.SpectrumOptions.From(visualization.Spectrum);
        OnPropertyChanged(nameof(ShowVisualizer));
        OnPropertyChanged(nameof(VisualizerFrameRateLimit));
    }

    // =====================================================================
    // Startup
    // =====================================================================
    public async Task InitializeAsync(bool loadInitialSource = true)
    {
        RefreshVisualizer();
        var stopwatch = Stopwatch.StartNew();
        await ReloadSourcesAsync();
        _host.Logger.Info($"Sources loaded in {stopwatch.ElapsedMilliseconds} ms.");

        _host.Watcher.Start(await _host.LibraryRepository.GetFoldersAsync());
        _positionTimer.Start();

        if (loadInitialSource)
        {
            var startupSource = ResolveStartupSource();
            // Remember the playlist id before SelectSourceAsync writes the newly selected
            // playlist back into settings; only that exact playlist may restore a selection.
            var restorePlaylistId = Settings.RestoreLastPlaylist ? Settings.LastPlaylistId : null;
            await SelectSourceAsync(startupSource);
            if (restorePlaylistId is { } playlistId)
            {
                RestoreLastPlayedSelection(playlistId);
            }

            _host.Logger.Info($"Initial playlist loaded in {stopwatch.ElapsedMilliseconds} ms total.");
        }

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

    /// <summary>
    /// Selects the last played track in the playlist restored at startup without starting
    /// playback. The window scrolls the row into view after applying its layout.
    /// </summary>
    private void RestoreLastPlayedSelection(long playlistId)
    {
        if (!_restoreSessionOnStartup || _isDisposed || !Settings.RestoreLastPlaylist)
        {
            return;
        }

        var path = Settings.LastPlayedTrackPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // Only the playlist that was actually restored may select a row; if it has been
        // deleted or replaced, leave the new source unselected.
        if (SelectedSource is not { Kind: SourceKind.Playlist } source || source.Playlist?.Id != playlistId)
        {
            return;
        }

        var row = Tracks.FirstOrDefault(r =>
            string.Equals(r.Track.Path, path, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            _host.Logger.Info($"Last played track is not in the restored playlist: {path}");
            return;
        }

        SelectedRow = row;
        _host.Logger.Info($"Restored last played row: {path}");
        SelectionRestored?.Invoke(this, row);
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

        // 媒体库数据可能刚发生变化（扫描、标签写入完成后都会走到这里），
        // 让标签补全候选缓存下次重新查询；晚一拍刷新不影响编辑功能。
        _host.TagSuggestions.Invalidate();
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

    /// <summary>
    /// Number of source switches currently loading. Reference-counted so a queued switch (the
    /// selection gate serializes them) keeps the overlay up while the previous one finishes.
    /// </summary>
    private int _loadingRequestCount;

    private void BeginLoading()
    {
        _loadingRequestCount++;
        IsLoading = true;
    }

    private void EndLoading()
    {
        if (_loadingRequestCount > 0)
        {
            _loadingRequestCount--;
        }

        if (_loadingRequestCount == 0)
        {
            IsLoading = false;
        }
    }

    public async Task SelectSourceAsync(SourceNodeViewModel? source)
    {
        if (source is null || _isDisposed)
        {
            return;
        }

        BeginLoading();
        var switchStopwatch = Stopwatch.StartNew();
        double gateMilliseconds = 0;
        double databaseMilliseconds = 0;
        try
        {
            // Paint the overlay before any of the blocking stages start: its fade-in has to
            // animate on screen while the database query and the row rebuild run. Idle priority
            // flushes the pending render (including the overlay's first animation frame) before
            // the dispatcher continues with this method.
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (_isDisposed)
            {
                return;
            }

            await _selectionGate.WaitAsync();
            try
            {
                if (_isDisposed)
                {
                    return;
                }

                gateMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

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

                var loadStopwatch = Stopwatch.StartNew();
                // Microsoft.Data.Sqlite's async API actually completes synchronously, so awaiting
                // the query on the dispatcher would freeze the overlay animation for the whole
                // read. Run it on the pool instead; SQLite/Dapper connections are not
                // thread-affine and every query opens its own connection.
                var tracks = await Task.Run(() => LoadTracksForSourceAsync(source));
                databaseMilliseconds = loadStopwatch.Elapsed.TotalMilliseconds;

                // Give the overlay a frame after the database read; row construction and the view
                // rebuild that follow are the expensive stages of the switch.
                await Dispatcher.Yield(DispatcherPriority.Render);

                var newLayout = source.Kind == SourceKind.Playlist && source.Playlist is not null
                    ? ViewLayoutFactory.Deserialize(source.Playlist.ViewLayoutJson)
                    : ViewLayoutFactory.Normalize(Settings.LibraryLayout.Clone());

                await ApplyTracksAsync(tracks, source, newLayout, databaseMilliseconds);

                _activeSource = source;
                if (source.Kind == SourceKind.Playlist && source.Playlist is not null)
                {
                    Settings.LastPlaylistId = source.Playlist.Id;
                }

                QueueSettingsSave();
                _ = RefreshStaleTracksAsync(force: true);
            }
            finally
            {
                _selectionGate.Release();
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Selecting source failed", ex);
            SetStatus($"加载失败：{ex.Message}");
        }
        finally
        {
            EndLoading();
            _host.Logger.Info(
                $"List switch overlay: '{source.Name}' visible={switchStopwatch.Elapsed.TotalMilliseconds:F1}ms, " +
                $"gateWait={gateMilliseconds:F1}ms, dbLoad={databaseMilliseconds:F1}ms");
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

    /// <summary>
    /// Builds the row view models off the UI thread. <see cref="TrackRowViewModel"/> is plain
    /// managed state (no DependencyObject / dispatcher affinity), so constructing thousands of
    /// rows does not need the UI thread; the caller applies the playing-row state afterwards.
    /// </summary>
    private static List<TrackRowViewModel> BuildTrackRows(IReadOnlyList<Track> tracks)
    {
        var rows = new List<TrackRowViewModel>(tracks.Count);
        for (var i = 0; i < tracks.Count; i++)
        {
            rows.Add(new TrackRowViewModel(tracks[i], i + 1));
        }

        return rows;
    }

    /// <summary>
    /// Replaces the visible tracks and rebuilds both collection views for a source switch. The work
    /// is staged (row build → collection swap → layout → sort/filter refresh → window column
    /// rebuild) and the dispatcher is allowed to render the loading overlay between the stages, so
    /// a slow switch never looks frozen.
    /// </summary>
    private async Task ApplyTracksAsync(
        IReadOnlyList<Track> tracks,
        SourceNodeViewModel source,
        PlaylistViewLayout? newLayout = null,
        double databaseMilliseconds = 0)
    {
        var switchStopwatch = Stopwatch.StartNew();
        var playingPath = CurrentRow?.Track.Path;
        _shuffleOrder = null;

        // Resolve the requested order before going to the background: the sorted row list is
        // handed to the collection views as-is (see ApplyViewGrouping), so the views never have to
        // sort on the dispatcher. That sort used to be the dominant cost of a switch and blocked
        // the loading overlay for hundreds of milliseconds.
        var sortLayout = newLayout ?? CurrentLayout;
        var groupedView = IsGroupedViewActive;
        var primarySort = BuildPrimarySort(sortLayout);
        var sortDescriptions = groupedView ? BuildGroupedSort(primarySort, sortLayout) : primarySort;

        var (rows, playingIndex, buildRowsMilliseconds, sortRowsMilliseconds) = await Task.Run(() =>
        {
            var buildStopwatch = Stopwatch.StartNew();
            var built = BuildTrackRows(tracks);
            var buildMilliseconds = buildStopwatch.Elapsed.TotalMilliseconds;

            var sortStopwatch = Stopwatch.StartNew();
            var sorted = TrackRowSorter.Sort(built, sortDescriptions);
            var sortMilliseconds = sortStopwatch.Elapsed.TotalMilliseconds;

            var index = playingPath is null
                ? -1
                : sorted.FindIndex(row =>
                    string.Equals(row.Track.Path, playingPath, StringComparison.OrdinalIgnoreCase));
            return (sorted, index, buildMilliseconds, sortMilliseconds);
        });
        var backgroundMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

        if (playingIndex >= 0)
        {
            var row = rows[playingIndex];
            row.IsPlaying = true;
            CurrentRow = row;
            // The row was rebuilt (for example after a metadata repair scan), so refresh
            // the now-playing panel from the new track data instead of the stale row.
            UpdateNowPlayingMetadata(row);
            BeginNowPlayingCoverLoad(tracks[playingIndex]);
        }

        // Let the overlay render after the background row build.
        await Dispatcher.Yield(DispatcherPriority.Render);

        double replaceAllMilliseconds;
        double layoutMilliseconds;
        double sortMilliseconds;
        double filterMilliseconds;
        double groupedRefreshMilliseconds;
        double tracksRefreshMilliseconds;
        double viewRefreshMilliseconds;

        // Both views stay deferred across the swap and the descriptor/filter updates: the Reset
        // produced by ReplaceAll and every SortDescriptions/GroupDescriptions/Filter change would
        // otherwise each trigger a full re-sort/re-group of the list (the dominant cost). The two
        // scopes are disposed explicitly so the log can attribute the refresh cost to each view.
        var groupedViewDefer = _groupedTracksView.DeferRefresh();
        var tracksViewDefer = _tracksView.DeferRefresh();
        try
        {
            // One Reset instead of Clear() + N×Add(): every intermediate notification made both
            // collection views (and the DataGrid) re-run their sorting/filtering/regeneration work.
            Tracks.ReplaceAll(rows);
            ResetVisibleGroupCovers();
            _tracksVersion++;
            replaceAllMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

            // Apply the new layout only after the swap: OnCurrentLayoutChanged rebuilds the grouped
            // row cells, and doing that on the old rows would waste the rebuild on rows that are
            // about to be replaced.
            if (newLayout is not null)
            {
                CurrentLayout = newLayout;
            }

            PlaylistTitle = source.Kind switch
            {
                SourceKind.Library => "媒体库",
                SourceKind.Folder => source.Folder?.DisplayName ?? source.Name,
                _ => source.Name
            };
            _basePlaylistSubtitle = BuildSubtitle(source, tracks.Count);
            layoutMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

            // The rows are already ordered: switch the views to "collection order" mode and only
            // (re)apply the grouping. Letting ApplySortAndGrouping run here would re-sort the whole
            // list on the dispatcher and make the optimization pointless.
            _viewUsesCollectionOrder = true;
            ApplyViewGrouping();

            // The swapped rows are brand-new view models with an empty cell layout. When the
            // layout object itself did not change (for example the scan-triggered refresh that
            // reuses the current layout), nothing else rebuilds the grouped cells, leaving the
            // album list's rows blank until the next layout change. Rebuild them here: the
            // signature includes _tracksVersion, so this always runs after a swap.
            RebuildGroupedView();
            sortMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;
            ApplyFilter();
            filterMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;
            // 新页面右侧列表与当前列表共用同一批行对象，切源后顺序快照必须跟着换。
            RebuildNowPlayingPlaylist();
        }
        finally
        {
            // Refresh the album list first (it is the visible view when grouping is active), then
            // the hidden flat DataGrid.
            groupedViewDefer.Dispose();
            groupedRefreshMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;
            tracksViewDefer.Dispose();
            tracksRefreshMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;
        }

        // Disposing the defer scopes above refreshes each view exactly once with the final state.
        viewRefreshMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        var layoutChangedMilliseconds = switchStopwatch.Elapsed.TotalMilliseconds;

        // Render the rebuilt list once before the overlay fades out in the caller.
        await Dispatcher.Yield(DispatcherPriority.Render);

        _host.Logger.Info(
            $"List switch: {source.Kind} '{source.Name}' rows={rows.Count}, " +
            $"dbLoad={databaseMilliseconds:F1}ms, " +
            $"buildRows={buildRowsMilliseconds:F1}ms, " +
            $"sortRows={sortRowsMilliseconds:F1}ms, " +
            $"replaceAll={replaceAllMilliseconds - backgroundMilliseconds:F1}ms, " +
            $"layout={layoutMilliseconds - replaceAllMilliseconds:F1}ms, " +
            $"sortGroup={sortMilliseconds - layoutMilliseconds:F1}ms, " +
            $"filter={filterMilliseconds - sortMilliseconds:F1}ms, " +
            $"groupedRefresh={groupedRefreshMilliseconds - filterMilliseconds:F1}ms, " +
            $"tracksRefresh={tracksRefreshMilliseconds - groupedRefreshMilliseconds:F1}ms, " +
            $"viewRefresh={viewRefreshMilliseconds - filterMilliseconds:F1}ms, " +
            $"columns={layoutChangedMilliseconds - viewRefreshMilliseconds:F1}ms, " +
            $"total={switchStopwatch.Elapsed.TotalMilliseconds:F1}ms");

        LogCoverMemory("list switch");
    }

    /// <summary>
    /// 把封面内存账本写进日志：缓存条目 / 估算占用 / 当前挂在界面上的相册组封面数。
    /// 排查“滚动越久内存越高”时看这一行就能分清是缓存没上限，还是界面上的引用没释放。
    /// </summary>
    private void LogCoverMemory(string stage)
    {
        var stats = _host.CoverImages.MemoryCacheStats;
        int visibleGroups;
        lock (_visibleGroupCoverGate)
        {
            visibleGroups = _visibleGroupCoverNodes.Count;
        }

        _host.Logger.Debug(
            $"Cover memory ({stage}): cache={stats.Entries} entries / {stats.Bytes / 1024.0 / 1024.0:F1} MB " +
            $"(limit {CoverImageService.MemoryCacheByteLimit / 1024 / 1024} MB), " +
            $"groupCovers={visibleGroups}/{VisibleGroupCoverLimit}");
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
        // Interactive sort/grouping changes go through descriptor sorting again: the collection
        // order is stale after that until the next source switch pre-sorts the rows.
        _viewUsesCollectionOrder = false;

        var primarySort = BuildPrimarySort();
        var grouped = IsGroupedViewActive;

        // Every descriptor mutation normally makes ListCollectionView.RefreshOrDefer() re-sort and
        // re-group the whole list. Deferring both views coalesces the changes into a single refresh
        // per view (the explicit Refresh() calls this used to do were a second full refresh).
        using (_tracksView.DeferRefresh())
        using (_groupedTracksView.DeferRefresh())
        {
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
        }

        RebuildGroupedView();
        RebuildNowPlayingPlaylist();
    }

    /// <summary>
    /// Applies the album grouping without touching the row order. Used after a source switch,
    /// where <see cref="TrackRowSorter"/> already ordered the rows on a background thread; the
    /// views stay free of sort descriptors so their single refresh never re-sorts the list.
    /// </summary>
    private void ApplyViewGrouping()
    {
        var grouped = IsGroupedViewActive;
        _tracksView.SortDescriptions.Clear();
        _tracksView.GroupDescriptions.Clear();
        _groupedTracksView.SortDescriptions.Clear();
        _groupedTracksView.GroupDescriptions.Clear();
        if (grouped)
        {
            // The rows were sorted so that one album's tracks are contiguous; ListCollectionView
            // creates the groups in first-appearance order, exactly like the descriptor-based sort
            // produced before.
            _groupedTracksView.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(TrackRowViewModel.AlbumGroupKey)));
        }
    }

    private IReadOnlyList<SortDescription> BuildPrimarySort() => BuildPrimarySort(CurrentLayout);

    private static IReadOnlyList<SortDescription> BuildPrimarySort(PlaylistViewLayout layout)
    {
        var hasSort = !string.IsNullOrWhiteSpace(layout.SortColumn);
        var sortColumn = hasSort ? layout.SortColumn! : PlaylistSortPolicy.DefaultOrder;
        var direction = hasSort && layout.SortDescending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        return [new SortDescription(sortColumn, direction)];
    }

    /// <summary>
    /// Sort order of the grouped list: the album-oriented primary sort first, then the
    /// album group key so one album's tracks always stay contiguous, and finally the
    /// disc/track order inside each group (foobar2000-style disc → track ascending).
    /// </summary>
    private List<SortDescription> BuildGroupedSort(IReadOnlyList<SortDescription> primary) =>
        BuildGroupedSort(primary, CurrentLayout);

    private static List<SortDescription> BuildGroupedSort(
        IReadOnlyList<SortDescription> primary,
        PlaylistViewLayout layout)
    {
        var sort = primary.ToList();
        var direction = primary[0].Direction;
        sort.Add(new SortDescription(nameof(TrackRowViewModel.AlbumGroupKey), direction));
        if (layout.GroupSortByDiscTrack)
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
    /// Restores descriptor sorting after the DataGrid rebuilds its columns, but only when the
    /// collection itself is not already in the requested order. Source switches pre-sort the rows
    /// (<see cref="TrackRowSorter"/>), so there is nothing to restore for them — re-adding the
    /// descriptors would re-sort the whole list on the dispatcher for no reason.
    /// </summary>
    public void ReapplySort()
    {
        if (_viewUsesCollectionOrder)
        {
            // Rebinding the grid's ItemsSource dropped any stale descriptors; the collection order
            // is authoritative now. The grouped ListView keeps its own descriptors untouched.
            using (_tracksView.DeferRefresh())
            using (_groupedTracksView.DeferRefresh())
            {
                _tracksView.SortDescriptions.Clear();
                _groupedTracksView.SortDescriptions.Clear();
            }
        }
        else if (!IsGroupedViewActive)
        {
            using (_tracksView.DeferRefresh())
            {
                ApplySortDescriptions(_tracksView, BuildPrimarySort());
            }
        }

        RebuildGroupedView();
        RebuildNowPlayingPlaylist();
    }

    /// <summary>
    /// Rebuilds the grouped list's column header cells and the per-row cell layout. The
    /// grouped view draws the album cover in a fixed gutter next to the whole group
    /// (foobar2000 style), so the cover column is not part of the row cells.
    /// </summary>
    private void RebuildGroupedView()
    {
        var rebuildStopwatch = Stopwatch.StartNew();
        var grouped = IsGroupedViewActive;
        var signature = BuildGroupedRebuildSignature(grouped);
        if (string.Equals(signature, _groupedRebuildSignature, StringComparison.Ordinal))
        {
            // Layout writes call this twice (CurrentLayout setter + ApplySortAndGrouping) and width
            // drags persist repeatedly. Nothing the rebuild derives from changed, so keep the
            // current headers and row-cell layout instead of rebuilding them again.
            return;
        }

        _groupedRebuildSignature = signature;

        var formats = ResolveSecondaryFormats();
        GroupedLeftColumns.Clear();
        GroupedRightColumns.Clear();
        var secondaryStopwatch = Stopwatch.StartNew();
        ApplySecondaryTexts(formats);
        var secondaryMilliseconds = secondaryStopwatch.Elapsed.TotalMilliseconds;

        if (!grouped)
        {
            foreach (var row in Tracks)
            {
                row.SetCellLayout(GroupedCellLayout.Empty);
            }

            GroupedColumnsRebuilt?.Invoke(this, EventArgs.Empty);
            _host.Logger.Debug(
                $"RebuildGroupedView: flat rows={Tracks.Count}, secondary={secondaryMilliseconds:F1}ms, " +
                $"elapsed={rebuildStopwatch.Elapsed.TotalMilliseconds:F1}ms");
            return;
        }

        var sortColumn = CurrentLayout.SortColumn;
        var descending = CurrentLayout.SortDescending;
        var leftHeaders = new List<GroupedColumnHeaderViewModel>();
        var leftTemplates = new List<System.Windows.DataTemplate?>();
        var rightHeaders = new List<GroupedColumnHeaderViewModel>();
        var rightTemplates = new List<System.Windows.DataTemplate?>();
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
                rightHeaders.Add(header);
                rightTemplates.Add(template);
            }
            else
            {
                GroupedLeftColumns.Add(header);
                leftHeaders.Add(header);
                leftTemplates.Add(template);
            }
        }

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

        // The layout is immutable and shared by every row: only rows the virtualized list actually
        // displays materialize their own TrackCellViewModel list, invisible rows just hold the ref.
        var cellLayout = new GroupedCellLayout(leftHeaders, leftTemplates, rightHeaders, rightTemplates);
        var boundRows = 0;
        foreach (var row in Tracks)
        {
            row.SetCellLayout(cellLayout);
            if (row.HasResolvedCells)
            {
                boundRows++;
            }
        }

        GroupedColumnsRebuilt?.Invoke(this, EventArgs.Empty);
        _host.Logger.Debug(
            $"RebuildGroupedView: grouped rows={Tracks.Count}, cols={leftHeaders.Count}+{rightHeaders.Count}, " +
            $"boundRows={boundRows}, secondary={secondaryMilliseconds:F1}ms, " +
            $"elapsed={rebuildStopwatch.Elapsed.TotalMilliseconds:F1}ms");
    }

    /// <summary>
    /// Signature of every input the grouped-view rebuild derives headers or row cells from:
    /// the track set, flat/grouped mode, sort and each column's visibility/pin/width and
    /// secondary format. An unchanged signature makes the rebuild a no-op.
    /// </summary>
    private string BuildGroupedRebuildSignature(bool grouped)
    {
        var builder = new StringBuilder(256);
        builder.Append(_tracksVersion)
            .Append('|').Append(grouped ? 'G' : 'F')
            .Append('|').Append(CurrentLayout.ShowSecondaryInfo ? 'S' : 's')
            .Append('|').Append(CurrentLayout.SortColumn ?? string.Empty)
            .Append('|').Append(CurrentLayout.SortDescending ? 'D' : 'A')
            .Append('|').Append(CurrentLayout.GroupSortByDiscTrack ? 'T' : 't');
        foreach (var column in CurrentLayout.Columns)
        {
            builder.Append('|').Append(column.Key)
                .Append(':').Append(column.Visible ? '1' : '0')
                .Append(':').Append((int)column.Pin)
                .Append(':').Append(column.Width.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(column.SecondaryFormat ?? string.Empty);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Resolves the current secondary-text formats and reuses the previous table when the result
    /// is unchanged. Rows invalidate their cached texts only when the shared instance differs, so
    /// an unrelated sort/layout refresh no longer invalidates every row.
    /// </summary>
    private IReadOnlyDictionary<string, string?> ResolveSecondaryFormats()
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

        var signature = formats.Count == 0
            ? string.Empty
            : string.Join('\u001e', formats.Select(pair => $"{pair.Key}\u001f{pair.Value}"));
        if (string.Equals(signature, _secondaryFormatsSignature, StringComparison.Ordinal))
        {
            return _secondaryFormats;
        }

        _secondaryFormats = formats;
        _secondaryFormatsSignature = signature;
        return formats;
    }

    /// <summary>
    /// Hands the shared secondary-format table to all rows. Formatting itself is deferred to the
    /// rows: this pass only swaps references and lets already displayed rows refresh their binding.
    /// </summary>
    private void ApplySecondaryTexts(IReadOnlyDictionary<string, string?> formats)
    {
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
        // Presets describe columns only; the layout factory keeps the user's current
        // grouped-cover size and per-group disc/track ordering preference, and turns the
        // second line on when the preset defines visible secondary formats.
        CurrentLayout = ViewLayoutFactory.ApplyPreset(CurrentLayout, preset);
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
        // 波形模式下从切歌那一刻就显示占位动画，覆盖解码 + 分析开始前的空窗，
        // 而不是等 StartWaveformLoad 才亮起来（两者之间通常还有数百毫秒）。
        if (IsWaveformMode && File.Exists(row.Track.Path))
        {
            IsWaveformLoading = true;
        }

        try
        {
            await _host.Player.LoadAsync(row.Track.Path, playWhenReady: true);
            IsPlaying = true;
            OutputDescription = _host.Player.OutputDescription;
            _consecutiveFailures = 0;
            SetStatus($"正在播放：{row.Title}");
            if (!string.Equals(Settings.LastPlayedTrackPath, row.Track.Path, StringComparison.OrdinalIgnoreCase))
            {
                Settings.LastPlayedTrackPath = row.Track.Path;
                QueueSettingsSave();
            }
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
            // 播放失败时波形不会再开始加载，把占位动画收掉，避免进度条一直“转”。
            CancelWaveformLoad();
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
            // End of the playable list: there is nothing to continue with, so return to the
            // idle state exactly like pressing Stop instead of leaving a stopped track displayed.
            Stop();
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
            // A shuffled round with a single playable track has nothing else to continue with.
            Stop();
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
        IsNowPlayingCoverLoading = false;
        CurrentCover = null;
        ClearNowPlayingGlow();
        ResetNowPlayingPageMetadata();
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

        CurrentInfo = string.Join(" · ", parts);
        DurationSeconds = track.DurationMs / 1000.0;
        DurationText = DisplayFormatter.Duration(track.DurationMs);

        // 「正在播放」页的标签卡片与格式徽标。
        NowPlayingAlbumArtist = TagOrDash(track.AlbumArtist);
        NowPlayingComposer = TagOrDash(track.Composer);
        NowPlayingGenre = TagOrDash(track.Genre);
        NowPlayingYear = track.Year is > 0
            ? track.Year.Value.ToString(CultureInfo.InvariantCulture)
            : EmptyTagText;
        NowPlayingTrackNumber = track.TrackNumber > 0
            ? track.TrackNumber.ToString(CultureInfo.InvariantCulture)
            : EmptyTagText;
        NowPlayingDiscNumber = track.DiscNumber > 0
            ? track.DiscNumber.ToString(CultureInfo.InvariantCulture)
            : EmptyTagText;
        NowPlayingCodec = TagOrDash(track.FormatDisplay);
        NowPlayingComment = TagOrDash(track.Comment);
        NowPlayingPath = track.Path;
        NowPlayingFormatText = BuildFormatBadgeText(track);
    }

    private static string TagOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? EmptyTagText : value.Trim();

    private static string BuildFormatBadgeText(Track track)
    {
        var parts = new List<string> { track.FormatDisplay };
        var bitrate = DisplayFormatter.Bitrate(track.Bitrate);
        var sampleRate = DisplayFormatter.SampleRate(track.SampleRate);
        if (!string.IsNullOrEmpty(bitrate))
        {
            parts.Add(string.IsNullOrEmpty(sampleRate) ? bitrate : $"{bitrate} / {sampleRate}");
        }
        else if (!string.IsNullOrEmpty(sampleRate))
        {
            parts.Add(sampleRate);
        }

        return string.Join(" · ", parts);
    }

    private void ResetNowPlayingPageMetadata()
    {
        NowPlayingAlbumArtist = EmptyTagText;
        NowPlayingComposer = EmptyTagText;
        NowPlayingGenre = EmptyTagText;
        NowPlayingYear = EmptyTagText;
        NowPlayingTrackNumber = EmptyTagText;
        NowPlayingDiscNumber = EmptyTagText;
        NowPlayingCodec = EmptyTagText;
        NowPlayingComment = EmptyTagText;
        NowPlayingPath = string.Empty;
        NowPlayingFormatText = EmptyTagText;
        NowPlayingOutputText = "未建立输出";
        NowPlayingOutputTooltip = string.Empty;
    }

    /// <summary>
    /// 刷新「正在播放」页的输出格式徽标。徽标取“最终送到硬件”的格式：独占模式显示与设备
    /// 协商出的客户端流（绕过引擎，直达端点），共享模式显示 Windows 引擎转换后的设备端点格式。
    /// 输出会在后台线程重建（切歌 / 换设备 / 切共享-独占），这里只读播放器的无锁快照，
    /// 并按字符串比较避免每 200ms 都触发一次绑定通知。
    /// </summary>
    private void UpdateNowPlayingOutputInfo()
    {
        var format = _host.Player.OutputFormat;
        if (format is null)
        {
            SetNowPlayingOutputInfo("未建立输出", string.Empty);
            return;
        }

        var modeText = format.Mode == OutputMode.Exclusive ? "独占" : "共享";
        var bitDepthText = format.IsFloat
            ? "32-bit float"
            : format.ContainerBitsPerSample is int container && container > format.BitsPerSample
                ? $"{format.BitsPerSample}-bit（{container}-bit 容器）"
                : $"{format.BitsPerSample}-bit";
        var channelsText = format.Channels switch
        {
            1 => "单声道",
            2 => "立体声",
            _ => $"{format.Channels}ch"
        };
        var clientText = $"{modeText} · {format.SampleRate / 1000.0:0.#} kHz · {bitDepthText} · {channelsText}";

        // 最终送到硬件的格式：独占模式协商出的客户端流直达设备端点；共享模式由 Windows
        // 音频引擎把应用流转换成设备端点格式后再送硬件（读不到端点时退回应用流格式）。
        var endpointFormat = _host.Player.Pipeline?.EndpointFormat;
        var isExclusive = format.Mode == OutputMode.Exclusive;
        var badgeText = isExclusive || string.IsNullOrWhiteSpace(endpointFormat)
            ? clientText
            : $"{modeText} · {endpointFormat}";

        var details = new List<string> { $"设备：{format.DeviceName}" };
        if (isExclusive)
        {
            details.Add($"实际输出流（最终送往设备）：{clientText}");
        }
        else
        {
            details.Add($"应用输出流：{clientText}");
            if (!string.IsNullOrWhiteSpace(endpointFormat))
            {
                details.Add($"设备端点（最终输出）：{endpointFormat}");
            }
        }

        if (!string.IsNullOrEmpty(format.DeviceMixFormatDescription))
        {
            details.Add($"设备混音格式：{format.DeviceMixFormatDescription}");
        }

        if (CurrentRow is { } row)
        {
            var track = row.Track;
            var conversions = new List<string>();
            if (track.SampleRate > 0 && track.SampleRate != format.SampleRate)
            {
                conversions.Add(
                    $"重采样 {track.SampleRate / 1000.0:0.#} kHz → {format.SampleRate / 1000.0:0.#} kHz");
            }

            if (track.Channels > 0 && track.Channels != format.Channels)
            {
                conversions.Add($"声道 {track.Channels} → {format.Channels}");
            }

            if (!format.IsFloat)
            {
                var outputBits = format.ContainerBitsPerSample is int containerBits &&
                                 containerBits > format.BitsPerSample
                    ? $"{format.BitsPerSample}-bit（{containerBits}-bit 容器）"
                    : $"{format.BitsPerSample}-bit";
                conversions.Add($"位深转换 32-bit float → {outputBits}");
            }

            details.Add(conversions.Count == 0
                ? "输出采样率 / 声道 / 位深与歌曲一致，未做额外转换"
                : string.Join("；", conversions));
        }

        SetNowPlayingOutputInfo(
            badgeText,
            string.Join(Environment.NewLine, details) + Environment.NewLine + "点击查看完整音频处理管线");
    }

    private void SetNowPlayingOutputInfo(string text, string tooltip)
    {
        if (!string.Equals(NowPlayingOutputText, text, StringComparison.Ordinal))
        {
            NowPlayingOutputText = text;
        }

        if (!string.Equals(NowPlayingOutputTooltip, tooltip, StringComparison.Ordinal))
        {
            NowPlayingOutputTooltip = tooltip;
        }
    }


    /// <summary>
    /// 开始加载「正在播放」面板的大封面。这里刻意不立即清空 CurrentCover：旧封面会留在画面上，
    /// 直到新封面解码完成由视图交叉淡入（CrossFadeImage），切歌因此不会闪白或硬切。
    /// 新曲目没有封面时，加载完成会把 CurrentCover 置空，视图再淡出旧图露出占位符。
    /// </summary>
    private void BeginNowPlayingCoverLoad(Track track)
    {
        var version = ++_nowPlayingCoverVersion;
        // 有旧封面时转圈会被 ShowCoverLoadingSpinner 隐藏，这里照常置位以覆盖首图尚无封面的情况。
        IsNowPlayingCoverLoading = true;
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
            _host.Logger.Debug($"Now playing cover loaded: {track.Path} ({(image is null ? "none" : "ok")})");
            if (image is null)
            {
                // 无封面时用主题强调色派生柔光，页面不至于变成纯白 / 纯黑。
                ApplyGlowPalette(_host.NowPlayingGlow.CreateFallback());
            }
            else
            {
                _ = LoadNowPlayingGlowAsync(image, BuildGlowCacheKey(track), version);
            }
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

    private static string BuildGlowCacheKey(Track track) =>
        $"{track.Path}\u0000{track.CoverKey}\u0000{track.ModifiedAtUtc.Ticks}\u0000{track.FileSize}";

    private async Task LoadNowPlayingGlowAsync(System.Windows.Media.Imaging.BitmapSource cover, string cacheKey, int version)
    {
        try
        {
            var palette = await _host.NowPlayingGlow.CreateAsync(cover, cacheKey);
            if (version != _nowPlayingCoverVersion)
            {
                return;
            }

            ApplyGlowPalette(palette);
            _host.Logger.Debug("Now playing glow ready");
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Now playing glow failed: {ex.Message}");
        }
    }

    private void ApplyGlowPalette(CoverPalette palette)
    {
        NowPlayingGlowPrimary = Color.FromRgb(palette.Primary.R, palette.Primary.G, palette.Primary.B);
        NowPlayingGlowSecondary = Color.FromRgb(palette.Secondary.R, palette.Secondary.G, palette.Secondary.B);
        _host.Logger.Debug(
            $"Now playing glow palette: primary=#{palette.Primary.R:X2}{palette.Primary.G:X2}{palette.Primary.B:X2}, " +
            $"secondary=#{palette.Secondary.R:X2}{palette.Secondary.G:X2}{palette.Secondary.B:X2}, fallback={palette.UsedFallback}");
    }

    private void ClearNowPlayingGlow()
    {
        NowPlayingGlowPrimary = Colors.Transparent;
        NowPlayingGlowSecondary = Colors.Transparent;
    }

    /// <summary>
    /// 主题切换后调用。原始取色按封面缓存，命中时只重做主题归一化（亮度/彩度区间），
    /// 否则按当前封面重新取一次。
    /// </summary>
    public void RefreshNowPlayingGlow()
    {
        if (CurrentRow?.Track is not { } track)
        {
            ClearNowPlayingGlow();
            return;
        }

        if (CurrentCover is null)
        {
            ApplyGlowPalette(_host.NowPlayingGlow.CreateFallback());
            return;
        }

        var cacheKey = BuildGlowCacheKey(track);
        if (_host.NowPlayingGlow.Remap(cacheKey) is { } remapped)
        {
            ApplyGlowPalette(remapped);
            return;
        }

        _ = LoadNowPlayingGlowAsync(CurrentCover, cacheKey, _nowPlayingCoverVersion);
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
                track.ModifiedAtUtc, track.FileSize), GroupCoverDecodeWidth);
            if (image is not null)
            {
                row.GroupCoverImage = image;
                TrackVisibleGroupCover(row);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Group cover failed for {track.Path}: {ex.Message}");
        }
    }

    /// <summary>
    /// 只保留最近显示过的 <see cref="VisibleGroupCoverLimit"/> 组封面：WPF 的组容器在
    /// “滚动离开视口”时既不会触发 IsVisibleChanged(false) 也不会 Unloaded（组虚拟化只作用于
    /// 组内条目），所以释放必须由这里按“最近使用”主动做。
    /// </summary>
    private void TrackVisibleGroupCover(TrackRowViewModel row)
    {
        bool needTrim;
        lock (_visibleGroupCoverGate)
        {
            if (_visibleGroupCoverNodes.TryGetValue(row, out var existing))
            {
                _visibleGroupCovers.Remove(existing);
                _visibleGroupCoverNodes.Remove(row);
            }

            _visibleGroupCoverNodes[row] = _visibleGroupCovers.AddLast(row);
            needTrim = _visibleGroupCovers.Count > VisibleGroupCoverLimit;
        }

        if (needTrim)
        {
            ScheduleGroupCoverTrim();
        }
    }

    /// <summary>
    /// 回收按“滚动停下之后再批量做”：列表会把上千个相册组一起实例化，逐条摘图会不断
    /// 触发整表重新测量（实测每次滚动 2 s 以上），批量 + 去抖只付出一次布局代价。
    /// </summary>
    private void ScheduleGroupCoverTrim()
    {
        _groupCoverTrimTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(400),
            System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => TrimVisibleGroupCovers(),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _groupCoverTrimTimer.Stop();
        _groupCoverTrimTimer.Start();
    }

    private void TrimVisibleGroupCovers()
    {
        _groupCoverTrimTimer?.Stop();

        List<TrackRowViewModel>? stale = null;
        lock (_visibleGroupCoverGate)
        {
            while (_visibleGroupCovers.Count > VisibleGroupCoverTrimTarget && _visibleGroupCovers.First is { } oldest)
            {
                _visibleGroupCovers.RemoveFirst();
                _visibleGroupCoverNodes.Remove(oldest.Value);
                (stale ??= new List<TrackRowViewModel>()).Add(oldest.Value);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var row in stale)
        {
            row.GroupCoverImage = null;
        }

        // 回收之后打一行账本：滚动结束时的真实占用比“列表切换那一刻”更有参考价值。
        LogCoverMemory("group cover trim");
    }

    /// <summary>列表内容整体更换时调用：旧行已经不再显示，清掉“最近显示”的记录即可。</summary>
    private void ResetVisibleGroupCovers()
    {
        _groupCoverTrimTimer?.Stop();
        lock (_visibleGroupCoverGate)
        {
            _visibleGroupCovers.Clear();
            _visibleGroupCoverNodes.Clear();
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
                track.ModifiedAtUtc, track.FileSize), RowCoverDecodeWidth);
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

            if (IsRepeatOneEnabled)
            {
                // Single-track repeat: a natural end restarts the same track.
                await PlayRowAsync(CurrentRow);
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

        // 「正在播放」页的输出格式徽标：输出建立 / 重建后异步生效，跟着 200ms tick 对齐即可。
        UpdateNowPlayingOutputInfo();

        // 切歌时后台加载线程会持有播放器的锁（WASAPI 初始化 / 格式协商可能数百毫秒）。
        // 这里只取无阻塞快照：拿不到锁就跳过本帧，绝不让 UI 线程跟着卡住。
        if (!_host.Player.TryReadPlaybackSnapshot(out var state, out var position, out var duration))
        {
            return;
        }

        if (state is AudioPlaybackState.Playing or AudioPlaybackState.Paused)
        {
            if (duration > TimeSpan.Zero)
            {
                DurationSeconds = duration.TotalSeconds;
                DurationText = DisplayFormatter.Duration(duration);
            }

            PositionSeconds = Math.Clamp(position.TotalSeconds, 0, Math.Max(duration.TotalSeconds, 0.1));
            PositionText = DisplayFormatter.Duration(position);
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
        IsWaveformLoading = true;
        _ = LoadWaveformAsync(path, _waveformVersion, cts);
    }

    public void CancelWaveformLoad()
    {
        _waveformVersion++;
        _waveformCts?.Cancel();
        _waveformCts?.Dispose();
        _waveformCts = null;
        CurrentWaveform = null;
        IsWaveformLoading = false;
    }

    private async Task LoadWaveformAsync(string path, int version, CancellationTokenSource cts)
    {
        // Progress<T> 在 UI 线程构造，回调会自动回到 UI 线程；渐进快照只替换属性，
        // 控件自己负责追加几何。分析器每 250 列上报一次（约每秒数十次），这里按
        // ~80ms 合并一次 UI 更新：分析期间波形条仍然逐步变长，但避免频繁触发整层几何重建。
        var progressWatch = Stopwatch.StartNew();
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

            if (progressWatch.ElapsedMilliseconds < 80)
            {
                return;
            }

            progressWatch.Restart();
            CurrentWaveform = peaks;
            IsWaveformLoading = false;
        });

        try
        {
            var watch = Stopwatch.StartNew();
            var peaks = await _host.Waveforms.GetAsync(path, progress, cts.Token);
            watch.Stop();
            if (peaks is null || version != _waveformVersion || cts.IsCancellationRequested)
            {
                if (version == _waveformVersion)
                {
                    IsWaveformLoading = false;
                }

                return;
            }

            if (!string.Equals(CurrentRow?.Track.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CurrentWaveform = peaks;
            IsWaveformLoading = false;
            _host.Logger.Debug(
                $"Waveform ready: columns={peaks.ColumnCount} valid={peaks.ValidCount} elapsed={watch.ElapsedMilliseconds}ms");
        }
        catch (OperationCanceledException)
        {
            // 切歌 / 停止是预期路径，不记日志。
            if (version == _waveformVersion)
            {
                IsWaveformLoading = false;
            }
        }
        catch (Exception ex)
        {
            if (version == _waveformVersion)
            {
                IsWaveformLoading = false;
            }

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
        OnPropertyChanged(nameof(VolumeToolTip));

        if (_suppressVolumeSave)
        {
            return;
        }

        // 记住“当前设备 + 当前模式”的显示音量；实际增益还要乘模式上限（ADR-033）。
        OutputVolumeResolver.Remember(
            Settings, SelectedDevice?.Id, CurrentOutputMode, Math.Clamp(value / 100.0, 0, 1));
        ApplyEffectiveVolume();
        QueueSettingsSave();
    }

    partial void OnIsMutedChanged(bool value)
    {
        ApplyEffectiveVolume();
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(MuteToolTip));
    }

    partial void OnSharedVolumeCeilingChanged(double value) =>
        OnVolumeCeilingChanged(OutputMode.Shared, value);

    partial void OnExclusiveVolumeCeilingChanged(double value) =>
        OnVolumeCeilingChanged(OutputMode.Exclusive, value);

    private void OnVolumeCeilingChanged(OutputMode mode, double value)
    {
        if (_suppressVolumeCeilingSave)
        {
            return;
        }

        Settings.VolumeCeilings ??= new OutputVolumeCeilings();
        var normalized = OutputVolumeResolver.ClampCeiling(value / 100.0);
        if (mode == OutputMode.Exclusive)
        {
            Settings.VolumeCeilings.Exclusive = normalized;
        }
        else
        {
            Settings.VolumeCeilings.Shared = normalized;
        }

        ApplyEffectiveVolume();
        OnPropertyChanged(nameof(VolumeToolTip));
        QueueSettingsSave();
        SetStatus($"{(mode == OutputMode.Exclusive ? "独占" : "共享")}模式音量上限已设为 " +
                  $"{Math.Round(normalized * 100)}%（音量滑块的 100% 对应此上限）");
    }

    /// <summary>初始化音量上限与显示音量（ADR-033）；只读设置，不触发保存。</summary>
    private void InitializeVolumeState()
    {
        SyncVolumeCeilingsFromSettings();
        // 启动时设备列表还没枚举：优先用“记住的偏好设备”查记忆；跟随默认设备时等首次刷新再切换上下文。
        _volumeContextKey = BuildVolumeContextKey(Settings.OutputDeviceId, Settings.OutputMode);
        SetDisplayVolume(OutputVolumeResolver.ResolveDisplayVolume(
            Settings, Settings.OutputDeviceId, Settings.OutputMode));
        ApplyEffectiveVolume();
    }

    /// <summary>把设置里的音量上限同步到设置页滑块（百分比）。</summary>
    private void SyncVolumeCeilingsFromSettings()
    {
        _suppressVolumeCeilingSave = true;
        try
        {
            SharedVolumeCeiling =
                OutputVolumeResolver.ClampCeiling(Settings.VolumeCeilings.Shared) * 100;
            ExclusiveVolumeCeiling =
                OutputVolumeResolver.ClampCeiling(Settings.VolumeCeilings.Exclusive) * 100;
        }
        finally
        {
            _suppressVolumeCeilingSave = false;
        }
    }

    /// <summary>
    /// 设备 / 模式切换后切换音量记忆上下文：只有上下文变化才从记忆重新加载显示音量，
    /// 避免同一设备的普通刷新把用户正在拖动的音量重置（ADR-033）。
    /// </summary>
    private void ApplyVolumeForContext(string? deviceId, OutputMode mode)
    {
        var key = BuildVolumeContextKey(deviceId, mode);
        if (string.Equals(key, _volumeContextKey, StringComparison.OrdinalIgnoreCase))
        {
            ApplyEffectiveVolume();
            return;
        }

        _volumeContextKey = key;
        var display = OutputVolumeResolver.ResolveDisplayVolume(Settings, deviceId, mode);
        SetDisplayVolume(display);
        ApplyEffectiveVolume();
        _host.Logger.Info(
            $"Output volume context switched: device={deviceId ?? "(default)"}, mode={mode}, " +
            $"display={Math.Round(display * 100)}%, ceiling={Math.Round(CurrentVolumeCeiling * 100)}%, " +
            $"gain={_host.Player.Volume:0.###}.");
    }

    private void SetDisplayVolume(double displayVolume)
    {
        _suppressVolumeSave = true;
        try
        {
            Volume = Math.Clamp(displayVolume, 0, 1) * 100;
        }
        finally
        {
            _suppressVolumeSave = false;
        }

        OnPropertyChanged(nameof(VolumeToolTip));
    }

    /// <summary>把显示音量 × 当前模式上限写入播放器（静音时写 0）。</summary>
    private void ApplyEffectiveVolume()
    {
        var display = Math.Clamp(Volume / 100.0, 0, 1);
        var ceiling = CurrentVolumeCeiling;
        var gain = OutputVolumeResolver.GetEffectiveGain(display, ceiling);
        _host.Player.Volume = IsMuted ? 0 : gain;
        _host.Logger.Debug(
            $"Applied output volume: display={display:0.###} ceiling={ceiling:0.###} " +
            $"gain={gain:0.###} muted={IsMuted} mode={CurrentOutputMode}.");
    }

    private static string BuildVolumeContextKey(string? deviceId, OutputMode mode) =>
        $"{deviceId?.Trim() ?? "(default)"}|{mode}";

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>
    /// 按 UI 步进（5%）调整显示音量并解除静音；音量条滚轮与音量快捷键共用此逻辑。
    /// <paramref name="direction"/> 为正向上、为负向下。
    /// </summary>
    public void AdjustVolume(int direction)
    {
        if (direction == 0)
        {
            return;
        }

        IsMuted = false;
        Volume = OutputVolumeResolver.StepDisplayPercent(Volume, direction);
    }

    partial void OnShowAlbumPanelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaylistAlbumPanel));
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
            ApplyOutputDevices(_host.Player.GetOutputDevices(), changes: null, announce: false);
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Refreshing audio devices failed", ex);
        }
    }

    /// <summary>
    /// 设备监视器回调（可能来自 COM / 监视线程）：切回 UI 线程后刷新下拉框。
    /// 初始快照与打开窗口时的 <see cref="RefreshDevices"/> 重复，因此不播报。
    /// </summary>
    private void OnAudioDevicesChanged(object? sender, AudioDevicesChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(
                new Action(() => ApplyOutputDevices(e.Devices, e.Changes, announce: !e.IsInitialSnapshot)),
                DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Scheduling audio device refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 用新的设备快照刷新下拉框并重新应用输出配置。
    /// <para>
    /// 记住的偏好设备（<see cref="AppSettings.OutputDeviceId"/>）缺失时回退到系统默认设备，
    /// 但**不覆盖**设置里的偏好 ID：设备重新出现后监视器会再次选中它。
    /// </para>
    /// </summary>
    private void ApplyOutputDevices(
        IReadOnlyList<AudioDeviceInfo> devices,
        AudioDeviceChangeSet? changes,
        bool announce)
    {
        var previousSelection = SelectedDevice;
        var selection = OutputDeviceSelector.Select(devices, Settings.OutputDeviceId);
        var mode = Settings.OutputMode;

        _suppressOutputApply = true;
        try
        {
            Devices.Clear();
            foreach (var device in devices)
            {
                Devices.Add(device);
            }

            SelectedDevice = selection.Device;
            OutputModeIndex = mode == OutputMode.Exclusive ? 1 : 0;
        }
        finally
        {
            _suppressOutputApply = false;
        }

        // 偏好设备不可用时下发 null = 跟随系统默认；否则按偏好 ID 精确匹配。
        _host.Player.ConfigureOutput(selection.IsPreferredAvailable ? Settings.OutputDeviceId : null, mode);
        OutputDescription = ComposeOutputDescription(selection);
        // 切换设备 / 模式后应用该设备在该模式下的音量记忆（ADR-033）。
        ApplyVolumeForContext(selection.Device?.Id, mode);
        if (selection.IsPreferredAvailable)
        {
            _fallbackNoticeShown = false;
        }

        if (announce)
        {
            AnnounceDeviceChanges(previousSelection, selection, changes ?? AudioDeviceChangeDetector.Compare([], devices));
        }
        else
        {
            _host.Logger.Info(
                $"Audio devices refreshed: {devices.Count} device(s), selected " +
                $"{(selection.Device?.FriendlyName ?? "(none)")}{(selection.IsFallback ? " (fallback to default)" : string.Empty)}.");
            if (selection.IsFallback)
            {
                ShowFallbackNotice(selection);
            }
        }
    }

    /// <summary>
    /// 偏好设备缺失时在状态栏提示一次回退结果（启动 / 手动刷新都可能触发）；
    /// 设备恢复或用户重新选择设备后会重置，下次丢失可再次提示。
    /// </summary>
    private void ShowFallbackNotice(OutputDeviceSelection selection)
    {
        if (_fallbackNoticeShown)
        {
            return;
        }

        _fallbackNoticeShown = true;
        SetStatus($"上次使用的输出设备不可用，已回退到默认设备：{selection.Device?.FriendlyName}");
    }

    private void AnnounceDeviceChanges(
        AudioDeviceInfo? previousSelection,
        OutputDeviceSelection selection,
        AudioDeviceChangeSet changes)
    {
        if (Devices.Count == 0)
        {
            SetStatus("未检测到可用的输出设备");
            return;
        }

        if (selection.IsFallback)
        {
            _fallbackNoticeShown = true;
            SetStatus($"上次使用的输出设备不可用，已回退到默认设备：{selection.Device?.FriendlyName}");
            return;
        }

        var restoredPreferred =
            selection.HasPreference &&
            selection.IsPreferredAvailable &&
            (changes.Added.Any(d => string.Equals(d.Id, selection.PreferredDeviceId, StringComparison.OrdinalIgnoreCase)) ||
             !string.Equals(previousSelection?.Id, selection.Device?.Id, StringComparison.OrdinalIgnoreCase));
        if (restoredPreferred)
        {
            SetStatus($"已恢复上次使用的输出设备：{selection.Device?.FriendlyName}");
            return;
        }

        if (changes.Removed.Count > 0)
        {
            SetStatus($"输出设备已断开：{string.Join("、", changes.Removed.Select(d => d.FriendlyName))}");
            return;
        }

        if (changes.DefaultChanged)
        {
            SetStatus($"默认输出设备已切换：{changes.CurrentDefault?.FriendlyName ?? "系统默认"}");
            return;
        }

        if (changes.Added.Count > 0)
        {
            SetStatus($"检测到新的输出设备：{string.Join("、", changes.Added.Select(d => d.FriendlyName))}");
            return;
        }

        if (changes.Renamed.Count > 0)
        {
            SetStatus($"输出设备已更新：{string.Join("、", changes.Renamed.Select(d => d.FriendlyName))}");
        }
    }

    private string ComposeOutputDescription(OutputDeviceSelection? selection = null)
    {
        selection ??= OutputDeviceSelector.Select(Devices, Settings.OutputDeviceId);
        var modeText = OutputModeIndex == 1 ? "独占模式" : "共享模式";
        if (selection.Device is null)
        {
            return $"未检测到输出设备 · {modeText}";
        }

        var text = $"{selection.Device.FriendlyName} · {modeText}";
        if (selection.IsFallback)
        {
            text += "（上次使用的输出设备不可用，已回退到系统默认）";
        }

        return text;
    }

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (_suppressOutputApply || value is null)
        {
            return;
        }

        ApplyOutput(deviceChanged: true);
    }

    partial void OnOutputModeIndexChanged(int value)
    {
        if (_suppressOutputApply)
        {
            return;
        }

        ApplyOutput(deviceChanged: false);
    }

    private void ApplyOutput(bool deviceChanged)
    {
        var mode = OutputModeIndex == 1 ? OutputMode.Exclusive : OutputMode.Shared;
        Settings.OutputMode = mode;
        if (deviceChanged && SelectedDevice is not null)
        {
            // 只有用户显式选择设备才更新“上次使用的设备”；自动回退不能覆盖偏好。
            Settings.OutputDeviceId = SelectedDevice.Id;
        }

        var selection = OutputDeviceSelector.Select(Devices, Settings.OutputDeviceId);
        _host.Player.ConfigureOutput(selection.IsPreferredAvailable ? Settings.OutputDeviceId : null, mode);
        OutputDescription = ComposeOutputDescription(selection);
        // 用户显式切换设备 / 模式时切换到对应的音量记忆（ADR-033）。
        ApplyVolumeForContext(selection.Device?.Id, mode);
        QueueSettingsSave();
        SetStatus($"输出已切换：{selection.Device?.FriendlyName ?? "系统默认"} · {(mode == OutputMode.Exclusive ? "独占" : "共享")}");
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

            var changedTracks = 0;
            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                var result = await _host.Scanner.ScanFolderAsync(folder, progress, token);
                changedTracks += result.Added + result.Removed;
                _host.Logger.Info($"Folder scan {folder.Path}: +{result.Added}/-{result.Removed} in {result.Elapsed.TotalSeconds:F1}s");
            }

            // 扫描没有实际变化时不要重载列表：重启后的首次扫描通常什么也没变，
            // 重载会把启动时已经定位好的选中行滚出可视区域（还会白白重建一次列表）。
            if (changedTracks > 0)
            {
                await ReloadSourcesAsync();
                await RefreshCurrentSourceAsync(preserveSelection: true);
            }
            else
            {
                _host.Logger.Debug("Library scan found no changes; keeping the current list");
            }

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

        var selectedRowPath = preserveSelection ? SelectedRow?.Track.Path : null;
        var selectedPaths = preserveSelection
            ? Tracks.Where(t => IsRowSelected(t)).Select(t => t.Track.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        // SelectedRow is normally also flagged in SelectedRows, but startup restoration assigns
        // it directly before the controls have synchronized, so preserve it explicitly.
        if (selectedPaths is not null && selectedRowPath is not null)
        {
            selectedPaths.Add(selectedRowPath);
        }

        var source = SelectedSource;
        var loadStopwatch = Stopwatch.StartNew();
        var tracks = await LoadTracksForSourceAsync(source);
        var databaseMilliseconds = loadStopwatch.Elapsed.TotalMilliseconds;

        // The user may have switched to another source while the rows were loading; applying the
        // old rows now would overwrite the newly selected list.
        if (!IsSameSource(source, SelectedSource))
        {
            return;
        }

        await ApplyTracksAsync(tracks, source, databaseMilliseconds: databaseMilliseconds);
        if (selectedPaths is { Count: > 0 })
        {
            foreach (var row in Tracks.Where(r => selectedPaths.Contains(r.Track.Path)))
            {
                row.IsSelected = true;
            }
        }

        if (selectedRowPath is not null)
        {
            SelectedRow = Tracks.FirstOrDefault(r =>
                string.Equals(r.Track.Path, selectedRowPath, StringComparison.OrdinalIgnoreCase));
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
    private Task EditSmartRulesAsync() => EditSmartRulesCoreAsync(SelectedSource);

    /// <summary>
    /// 左侧来源列表右键菜单：明确作用于右键命中的播放列表。
    /// 右键不再切换 <see cref="SelectedSource"/>（切换会同步重建数千行，阻塞菜单弹出）。
    /// </summary>
    public Task EditSmartRulesFromMenuAsync(SourceNodeViewModel? node) => EditSmartRulesCoreAsync(node);

    private async Task EditSmartRulesCoreAsync(SourceNodeViewModel? source)
    {
        if (source?.Playlist is not { IsSmart: true } playlist)
        {
            return;
        }

        var wasActive = ReferenceEquals(source, SelectedSource);
        var definition = _dialogs.EditSmartPlaylist(playlist.Name, playlist.GetSmartDefinition());
        if (definition is null)
        {
            return;
        }

        await _host.PlaylistRepository.UpdatePlaylistRulesAsync(playlist.Id, definition);
        await ReloadSourcesAsync();

        // 只有正在浏览这个智能播放列表时才需要刷新右侧歌曲列表；
        // 编辑其它来源的规则不应让当前列表重载。
        if (wasActive)
        {
            await RefreshCurrentSourceAsync(preserveSelection: true);
        }

        SetStatus("智能播放列表条件已更新");
    }

    [RelayCommand]
    private Task RenamePlaylistAsync() => RenamePlaylistCoreAsync(SelectedSource);

    /// <summary>左侧来源列表右键菜单：作用于右键命中的播放列表，不要求它当前被选中。</summary>
    public Task RenamePlaylistFromMenuAsync(SourceNodeViewModel? node) => RenamePlaylistCoreAsync(node);

    private async Task RenamePlaylistCoreAsync(SourceNodeViewModel? source)
    {
        if (source?.IsRemovablePlaylist != true || source.Playlist is null)
        {
            SetStatus("默认播放列表或媒体库不能重命名");
            return;
        }

        var name = _dialogs.AskText("重命名播放列表", "名称", source.Playlist.Name);
        if (string.IsNullOrWhiteSpace(name) || name == source.Playlist.Name)
        {
            return;
        }

        if (PlaylistNameExists(name, source.Playlist.Id))
        {
            _dialogs.Info($"已经存在名为“{name}”的播放列表，请换一个名称。");
            return;
        }

        await _host.PlaylistRepository.RenamePlaylistAsync(source.Playlist.Id, name);
        await ReloadSourcesAsync();
        SetStatus("播放列表已重命名");
    }

    [RelayCommand]
    private Task DeletePlaylistAsync() => DeletePlaylistCoreAsync(SelectedSource);

    /// <summary>左侧来源列表右键菜单：作用于右键命中的播放列表，不要求它当前被选中。</summary>
    public Task DeletePlaylistFromMenuAsync(SourceNodeViewModel? node) => DeletePlaylistCoreAsync(node);

    private async Task DeletePlaylistCoreAsync(SourceNodeViewModel? source)
    {
        if (source?.IsRemovablePlaylist != true || source.Playlist is null)
        {
            SetStatus("默认播放列表不能删除");
            return;
        }

        var wasActive = ReferenceEquals(source, SelectedSource);
        if (!_dialogs.Confirm($"确定删除播放列表“{source.Playlist.Name}”吗？文件不会被删除。"))
        {
            return;
        }

        await _host.PlaylistRepository.DeletePlaylistAsync(source.Playlist.Id);
        await ReloadSourcesAsync();

        // 删除的是当前正在浏览的播放列表时才回退到媒体库；删除其它播放列表不应打断当前列表。
        if (wasActive)
        {
            await SelectSourceAsync(Sources.FirstOrDefault());
        }

        SetStatus("播放列表已删除");
    }

    /// <summary>
    /// 按给定顺序重排左侧来源列表中的播放列表节点（默认播放列表除外）并持久化。
    /// 只移动 <see cref="Sources"/> 尾部连续的播放列表节点，不重建集合，因此当前选中来源保持不变；
    /// 写库失败时重新加载来源列表，回退到数据库里的原顺序。
    /// </summary>
    public async Task ReorderPlaylistsAsync(IReadOnlyList<Playlist> orderedPlaylists)
    {
        if (orderedPlaylists.Count == 0 || _isDisposed)
        {
            return;
        }

        var playlistNodes = Sources.Where(s => s.Kind == SourceKind.Playlist && s.Playlist is not null).ToList();
        if (playlistNodes.Count != orderedPlaylists.Count)
        {
            _host.Logger.Warn($"Playlist reorder ignored: {orderedPlaylists.Count} ordered vs {playlistNodes.Count} in sources.");
            return;
        }

        var orderedNodes = orderedPlaylists
            .Select(playlist => playlistNodes.FirstOrDefault(node => node.Playlist!.Id == playlist.Id))
            .ToList();
        if (orderedNodes.Any(node => node is null))
        {
            _host.Logger.Warn("Playlist reorder ignored: ordered playlist is not present in sources.");
            return;
        }

        // ReloadSourcesAsync 会把所有播放列表放在文件夹节点之后，这里再确认一次连续性，
        // 避免在异常布局下用 Move 打乱“媒体库 / 播放列表”分组。
        var firstIndex = Sources.IndexOf(playlistNodes[0]);
        for (var i = 0; i < playlistNodes.Count; i++)
        {
            if (firstIndex < 0 || firstIndex + i >= Sources.Count || !ReferenceEquals(Sources[firstIndex + i], playlistNodes[i]))
            {
                _host.Logger.Warn("Playlist reorder ignored: playlist nodes are not contiguous in sources.");
                return;
            }
        }

        var selected = SelectedSource;
        _suppressSourceSelection = true;
        try
        {
            for (var i = 0; i < orderedNodes.Count; i++)
            {
                var node = orderedNodes[i]!;
                var currentIndex = Sources.IndexOf(node);
                if (currentIndex != firstIndex + i)
                {
                    Sources.Move(currentIndex, firstIndex + i);
                }
            }

            _sourcesView.Refresh();

            // 分组刷新可能让 ListBox 暂时清空 SelectedItem，这里把来源选择恢复回来。
            if (!ReferenceEquals(SelectedSource, selected))
            {
                SetSelectedSourceSilently(selected);
            }
        }
        finally
        {
            _suppressSourceSelection = false;
        }

        try
        {
            await _host.PlaylistRepository.UpdatePlaylistOrderAsync(
                orderedNodes.Select(node => node!.Playlist!.Id).ToList());
            var sortOrder = 1;
            foreach (var node in orderedNodes)
            {
                var playlist = node!.Playlist!;
                playlist.SortOrder = playlist.IsDefault ? 0 : sortOrder++;
            }

            SetStatus("播放列表顺序已保存");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Updating playlist order failed", ex);
            SetStatus($"保存播放列表顺序失败：{ex.Message}");
            await ReloadSourcesAsync();
        }
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

            await ApplyTagEditsAsync(rows, tags, rows.Select(_ => edit).ToList(), "标签");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Tag edit failed", ex);
            _dialogs.Error($"保存标签失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 「正在播放」页的标签属性入口：只编辑当前播放曲目，避免误用播放列表页残留的多选状态。
    /// </summary>
    public async Task EditCurrentTrackTagsAsync()
    {
        if (CurrentRow is not { } row)
        {
            return;
        }

        var track = row.Track;
        try
        {
            var tags = await Task.Run(() => new List<AudioTagData> { _host.TagService.Read(track.Path) });
            var edit = _dialogs.EditTags([track], tags);
            if (edit is null)
            {
                return;
            }

            await ApplyTagEditsAsync([row], tags, [edit], "标签");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Current track tag edit failed", ex);
            _dialogs.Error($"保存标签失败：{ex.Message}");
        }
    }

    /// <summary>Web 搜索标签（ADR-022）：选择字段与匹配后，复用本地标签写入/刷新闭环。</summary>
    [RelayCommand]
    private async Task SearchWebTagsAsync(TrackRowViewModel? row)
    {
        var rows = Tracks.Where(IsRowSelected).ToList();
        if (rows.Count <= 1)
        {
            row ??= SelectedRow;
            rows = row is null ? [] : [row];
        }

        if (rows.Count == 0)
        {
            _dialogs.Info("请先选择要搜索标签的歌曲。");
            return;
        }

        var tracks = rows.Select(r => r.Track).ToList();
        try
        {
            var plan = _dialogs.SearchWebTags(tracks);
            if (plan is null || plan.Edits.Count == 0)
            {
                return;
            }

            // 计划只包含真正有字段变化的文件；按路径找回对应行（窗口使用同一批 Track 对象）。
            var planned = new List<(TrackRowViewModel Row, TagBatchEdit Edit)>();
            foreach (var edit in plan.Edits)
            {
                var target = rows.FirstOrDefault(r =>
                    ReferenceEquals(r.Track, edit.Track) ||
                    string.Equals(r.Track.Path, edit.Track.Path, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                {
                    planned.Add((target, edit.Edit));
                }
            }

            if (planned.Count == 0)
            {
                SetStatus("没有可写入的 Web 标签字段");
                return;
            }

            var tags = await Task.Run(() =>
                planned.Select(p => _host.TagService.Read(p.Row.Track.Path)).ToList());
            await ApplyTagEditsAsync(
                planned.Select(p => p.Row).ToList(),
                tags,
                planned.Select(p => p.Edit).ToList(),
                "Web 标签");
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Web tag apply failed", ex);
            _dialogs.Error($"写入 Web 标签失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 标签规范化（ADR-025）：按设置页配置的正则规则批量改写选中歌曲的标签。
    /// ruleId 为 null 时应用全部启用规则；读取标签与规则匹配在后台完成，
    /// 写入复用 <see cref="ApplyTagEditsAsync"/> 的完整标签闭环（写文件、重新索引、刷新封面与列表）。
    /// </summary>
    public async Task ApplyTagNormalizationAsync(string? ruleId)
    {
        var rows = Tracks.Where(IsRowSelected).ToList();
        if (rows.Count == 0 && SelectedRow is not null)
        {
            rows.Add(SelectedRow);
        }

        if (rows.Count == 0)
        {
            SetStatus("请先选择要规范化的歌曲");
            return;
        }

        var configured = Settings.TagNormalizationRules;
        List<TagNormalizationRule> rules;
        string ruleLabel;
        if (ruleId is null)
        {
            rules = configured.Where(rule => rule.Enabled).Select(rule => rule.Clone()).ToList();
            ruleLabel = "全部启用规则";
        }
        else
        {
            var rule = configured.FirstOrDefault(item => string.Equals(item.Id, ruleId, StringComparison.Ordinal));
            if (rule is null)
            {
                SetStatus("找不到该规范化规则，可能已在设置页删除");
                return;
            }

            if (!rule.Enabled)
            {
                SetStatus($"规则「{rule.Name}」已停用");
                return;
            }

            rules = [rule.Clone()];
            ruleLabel = $"{rule.Id} / {rule.Name}";
        }

        // 非法正则的规则在 Core 里本来也会被跳过；这里提前过滤，
        // 避免整批歌曲都因“无变化”被跳过却没有任何提示。
        rules = rules.Where(rule => TagNormalization.IsValidPattern(rule.Pattern, out _)).ToList();
        if (rules.Count == 0)
        {
            SetStatus(ruleId is null ? "没有可用的规范化规则，请先在设置页检查" : "该规则的正则表达式无效，未应用");
            return;
        }

        SetStatus("正在检查标签…");
        try
        {
            var plan = await Task.Run(() =>
            {
                var targets = new List<TrackRowViewModel>();
                var sourceTags = new List<AudioTagData>();
                var edits = new List<TagBatchEdit>();
                var skipped = 0;
                var changes = 0;
                foreach (var row in rows)
                {
                    AudioTagData tag;
                    try
                    {
                        tag = _host.TagService.Read(row.Track.Path);
                    }
                    catch (Exception ex)
                    {
                        // 单个文件读标签失败不影响其余文件：记录后计入跳过数。
                        _host.Logger.Warn($"Tag normalization skipped (read failed) {row.Track.Path}: {ex.Message}");
                        skipped++;
                        continue;
                    }

                    var result = TagNormalization.ApplyToTag(rules, tag);
                    if (result is null || !result.HasChanges)
                    {
                        skipped++;
                        continue;
                    }

                    var edit = new TagBatchEdit();
                    result.ApplyTo(edit);
                    targets.Add(row);
                    sourceTags.Add(tag);
                    edits.Add(edit);
                    changes += result.Count;
                }

                return (Targets: targets, Tags: sourceTags, Edits: edits, Skipped: skipped, Changes: changes);
            });

            _host.Logger.Info(
                $"Tag normalization planned: rule={ruleLabel}, " +
                $"rules={string.Join(", ", rules.Select(r => $"{r.Id}/{r.Name}"))}, " +
                $"files={plan.Targets.Count}, changes={plan.Changes}, skipped={plan.Skipped}");

            if (plan.Targets.Count == 0)
            {
                SetStatus(plan.Skipped > 0
                    ? $"没有需要规范化的标签（检查了 {plan.Skipped} 首歌曲）"
                    : "没有需要规范化的标签");
                return;
            }

            var saved = await ApplyTagEditsAsync(plan.Targets, plan.Tags, plan.Edits, "标签规范化");
            if (saved == plan.Targets.Count)
            {
                SetStatus($"已规范化 {saved} 首歌曲的标签（{plan.Changes} 处修改）");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error("Tag normalization failed", ex);
            _dialogs.Error($"标签规范化失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 标签写入闭环：以每个文件当前标签为底稿合并编辑，写入后重新索引、失效封面缓存并刷新列表。
    /// 标签编辑与 Web 搜索共用，避免复制第二份写文件代码。
    /// </summary>
    private async Task<int> ApplyTagEditsAsync(
        IReadOnlyList<TrackRowViewModel> rows,
        IReadOnlyList<AudioTagData> tags,
        IReadOnlyList<TagBatchEdit> edits,
        string operation)
    {
        var saved = 0;
        var failures = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var target = rows[i];
            try
            {
                var model = TagBatchEditor.CreateEdit(tags[i], edits[i]);
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
            _dialogs.Error($"{operation}保存完成：成功 {saved} 个，失败 {failures.Count} 个。\n" +
                           string.Join("\n", failures.Take(5)));
            SetStatus($"{operation}保存：成功 {saved} 个，失败 {failures.Count} 个");
        }
        else
        {
            _host.Logger.Info($"{operation} saved: {saved} file(s)");
            SetStatus(saved > 1 ? $"已保存 {saved} 个文件的{operation}" : $"{operation}已保存");
        }

        return saved;
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
            var playlistRemoved = 0;
            if (deleted.Count > 0)
            {
                await _host.LibraryRepository.MarkTracksMissingAsync(deleted);
                try
                {
                    // ADR-021（A1 修订）：删除到回收站是用户确认过的删除，播放列表里的条目也一并移除，
                    // 避免列表里留下永远无法播放的缺失行。
                    playlistRemoved = await _host.PlaylistRepository.RemovePlaylistItemsByPathAsync(deleted);
                }
                catch (Exception ex)
                {
                    // 播放列表清理失败不阻断“已删除文件”状态：文件已经进回收站、曲目行已标记缺失，
                    // 这里只降级为 WARN，界面刷新与部分失败提示保持原行为。
                    _host.Logger.Warn($"Removing deleted files from playlists failed: {ex.Message}");
                }

                if (playlistRemoved > 0)
                {
                    // INFO 日志独立于“是否整体成功”分支记录移除数，部分失败时也能追溯。
                    _host.Logger.Info($"Playlist items removed for deleted files: {playlistRemoved} item(s)");
                }

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
                SetStatus(playlistRemoved > 0
                    ? $"已把 {deleted.Count} 个文件移到回收站，并从播放列表移除 {playlistRemoved} 项"
                    : $"已把 {deleted.Count} 个文件移到回收站");
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
            // 导入后 Settings.Current 是新的实例；把输出相关字段同步到视图模型持有的实例，
            // 随后的设备刷新才能按导入的偏好设备重新选择。
            Settings.OutputDeviceId = _host.Settings.Current.OutputDeviceId;
            Settings.OutputMode = _host.Settings.Current.OutputMode;
            Settings.Volume = _host.Settings.Current.Volume;
            Settings.DeviceVolumes = _host.Settings.Current.DeviceVolumes;
            Settings.VolumeCeilings = _host.Settings.Current.VolumeCeilings;
            SyncVolumeCeilingsFromSettings();
            // 强制接下来的设备刷新按导入的音量记忆重新加载显示音量（ADR-033）。
            _volumeContextKey = null;
            RefreshDevices();

            _suppressThemeApply = true;
            ThemeIndex = (int)_host.Settings.Current.Theme;
            _suppressThemeApply = false;
            ThemeManager.Apply(_host.Settings.Current.Theme);
            RefreshVisualizer();

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
        // 音量在调节时已按“设备 + 模式”即时记忆（ADR-033）；这里只同步全局回落音量。
        Settings.Volume = Math.Clamp(Volume / 100.0, 0, 1);
        Settings.OutputMode = OutputModeIndex == 1 ? OutputMode.Exclusive : OutputMode.Shared;
        // 不在这里写 Settings.OutputDeviceId：下拉框可能展示的是回退后的默认设备，
        // 写回会覆盖用户记住的偏好设备。
        Settings.ShowAlbumPanel = ShowAlbumPanel;
        await _host.Settings.SaveAsync();

        // 可视化性能验收数据：平均 / 最大每帧分析耗时写进日志（ADR-023 §10）。
        var stats = _host.Spectrum.Stats;
        if (stats.Frames > 0)
        {
            _host.Logger.Info(
                $"Spectrum stats: frames={stats.Frames}, avg={stats.AverageMilliseconds:F3} ms, " +
                $"max={stats.MaxMilliseconds:F3} ms, rate={stats.SampleRate}Hz, bands={stats.BandCount}");
        }
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
        _host.DeviceMonitor.DevicesChanged -= OnAudioDevicesChanged;
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
