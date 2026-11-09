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
    private readonly ICollectionView _sourcesView;
    private readonly DispatcherTimer _positionTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _watcherTimer;
    private readonly DispatcherTimer _settingsTimer;
    private readonly HashSet<string> _pendingWatchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _selectionGate = new(1, 1);

    private bool _suppressSourceSelection;
    private bool _suppressOutputApply;
    private bool _suppressVolumeSave;
    private bool _isSeeking;
    private bool _isDisposed;
    private int _consecutiveFailures;
    private CancellationTokenSource? _scanCts;

    public MainViewModel(AppHost host, IDialogService dialogs)
    {
        _host = host;
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = host.Settings.Current;

        Sources = [];
        _sourcesView = CollectionViewSource.GetDefaultView(Sources);
        _sourcesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SourceNodeViewModel.Section)));

        Tracks = [];
        _tracksView = new ListCollectionView(Tracks);
        _tracksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TrackRowViewModel.AlbumGroupKey)));
        TracksView = _tracksView;

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

        _host.Player.StateChanged += OnPlayerStateChanged;
        _host.Player.PlaybackEnded += OnPlaybackEnded;
        _host.Watcher.Changed += OnLibraryFileChanged;

        ShowAlbumPanel = Settings.ShowAlbumPanel;
        _suppressVolumeSave = true;
        Volume = Settings.Volume * 100;
        _suppressVolumeSave = false;
        OutputDescription = _host.Player.OutputDescription;
        StatusMessage = "就绪";
    }

    public AppSettings Settings { get; }
    public ObservableCollection<SourceNodeViewModel> Sources { get; }
    public ICollectionView SourcesView => _sourcesView;
    public ObservableCollection<TrackRowViewModel> Tracks { get; }
    public ICollectionView TracksView { get; }
    public ObservableCollection<AudioDeviceInfo> Devices { get; }
    public ObservableCollection<ColumnPreset> Presets { get; }

    public IReadOnlyList<string> OutputModeOptions { get; } = ["共享模式", "独占模式"];

    [ObservableProperty]
    private PlaylistViewLayout _currentLayout = ViewLayoutFactory.CreateDefault();

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

    [ObservableProperty]
    private double _volume = 75;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _showAlbumPanel = true;

    [ObservableProperty]
    private bool _groupByAlbum = true;

    [ObservableProperty]
    private string _statusMessage = "就绪";

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
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _hasTracks;

    public event EventHandler? LayoutChanged;
    public event EventHandler? SortChanged;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CurrentRowChanged;

    /// <summary>
    /// Set by the main window so layout changes can capture the live DataGrid column
    /// widths/order even when the switch is initiated from the view model.
    /// </summary>
    public Func<PlaylistViewLayout?>? LayoutProvider { get; set; }

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

    partial void OnCurrentRowChanged(TrackRowViewModel? value)
    {
        OnPropertyChanged(nameof(CurrentFormatText));
        OnPropertyChanged(nameof(CurrentFilePath));
        OnPropertyChanged(nameof(HasCurrentTrack));
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
        if (Settings.AutoScanOnStartup)
        {
            _ = ScanLibraryAsync(silent: true);
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
            PersistCurrentLayout();
            SetSelectedSourceSilently(source);

            var tracks = await LoadTracksForSourceAsync(source);
            CurrentLayout = source.Kind == SourceKind.Playlist && source.Playlist is not null
                ? ViewLayoutFactory.Deserialize(source.Playlist.ViewLayoutJson)
                : ViewLayoutFactory.Normalize(Settings.LibraryLayout.Clone());

            ApplyTracks(tracks, source);
            if (source.Kind == SourceKind.Playlist && source.Playlist is not null)
            {
                Settings.LastPlaylistId = source.Playlist.Id;
            }

            QueueSettingsSave();
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
        Tracks.Clear();
        for (var i = 0; i < tracks.Count; i++)
        {
            var row = new TrackRowViewModel(tracks[i], i + 1);
            if (playingPath is not null && string.Equals(tracks[i].Path, playingPath, StringComparison.OrdinalIgnoreCase))
            {
                row.IsPlaying = true;
                CurrentRow = row;
            }

            Tracks.Add(row);
        }

        PlaylistTitle = source.Kind switch
        {
            SourceKind.Library => "媒体库",
            SourceKind.Folder => source.Folder?.DisplayName ?? source.Name,
            _ => source.Name
        };
        PlaylistSubtitle = BuildSubtitle(source, tracks.Count);
        HasTracks = Tracks.Count > 0;
        ApplySortAndGrouping();
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
        if (GroupByAlbum)
        {
            if (!_tracksView.GroupDescriptions.OfType<PropertyGroupDescription>()
                    .Any(g => g.PropertyName == nameof(TrackRowViewModel.AlbumGroupKey)))
            {
                _tracksView.GroupDescriptions.Clear();
                _tracksView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TrackRowViewModel.AlbumGroupKey)));
            }
        }
        else
        {
            _tracksView.GroupDescriptions.Clear();
        }

        _tracksView.SortDescriptions.Clear();
        if (!string.IsNullOrWhiteSpace(CurrentLayout.SortColumn))
        {
            _tracksView.SortDescriptions.Add(new SortDescription(CurrentLayout.SortColumn,
                CurrentLayout.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending));
        }
        else
        {
            _tracksView.SortDescriptions.Add(new SortDescription(nameof(TrackRowViewModel.RowNumber), ListSortDirection.Ascending));
        }

        _tracksView.Refresh();
    }

    partial void OnGroupByAlbumChanged(bool value)
    {
        ApplySortAndGrouping();
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
        ApplySortAndGrouping();
        PersistCurrentLayout();
    }

    // =====================================================================
    // Layout and presets
    // =====================================================================
    public void PersistCurrentLayout() => PersistCurrentLayout(null, captureFromView: true);

    /// <param name="capturedLayout">布局快照；为空且 <paramref name="captureFromView"/> 为真时向窗口取当前列宽/顺序。</param>
    /// <param name="captureFromView">
    /// 应用预设或列设置后必须为 false：此时界面还是旧列，抓取会把旧宽度覆盖回新布局。
    /// </param>
    public void PersistCurrentLayout(PlaylistViewLayout? capturedLayout, bool captureFromView = true)
    {
        if (capturedLayout is null && captureFromView)
        {
            capturedLayout = LayoutProvider?.Invoke();
        }

        if (capturedLayout is not null)
        {
            CurrentLayout = ViewLayoutFactory.Normalize(capturedLayout);
        }

        if (SelectedSource?.Kind == SourceKind.Playlist && SelectedSource.Playlist is not null)
        {
            var playlist = SelectedSource.Playlist;
            var layout = CurrentLayout;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _host.PlaylistRepository.UpdatePlaylistLayoutAsync(playlist.Id, ViewLayoutFactory.Serialize(layout));
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
        CurrentLayout = ViewLayoutFactory.Normalize(preset.ToLayout());
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

            var target = SelectedRow ?? Tracks.FirstOrDefault(r => r.IsPlayable);
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
        if (row is null || !row.IsPlayable)
        {
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
        DurationSeconds = row.Track.DurationMs / 1000.0;
        DurationText = DisplayFormatter.Duration(row.Track.DurationMs);
        PositionSeconds = 0;

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
            _ = LoadNowPlayingCoverAsync(row.Track);
            _ = LoadRowCoverAsync(row);
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

    [RelayCommand]
    private async Task PlayPreviousAsync()
    {
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
        CurrentTitle = "未在播放";
        CurrentSubtitle = string.Empty;
        CurrentInfo = string.Empty;
        CurrentCover = null;
        OnPropertyChanged(nameof(HasCurrentTrack));
    }

    private List<TrackRowViewModel> GetPlaybackOrder() =>
        _tracksView.Cast<TrackRowViewModel>().Where(r => r.IsPlayable).ToList();

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

    private async Task LoadNowPlayingCoverAsync(Track track)
    {
        try
        {
            var image = await _host.CoverImages.GetAsync(new CoverRequest(track.Path, track.CoverKey ?? string.Empty,
                track.ModifiedAtUtc, track.FileSize), 420);
            if (CurrentRow?.Track.Path == track.Path)
            {
                CurrentCover = image;
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"Now playing cover failed: {ex.Message}");
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
                track.ModifiedAtUtc, track.FileSize), 56);
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

    public void BeginSeek() => _isSeeking = true;

    public void CommitSeek(double seconds)
    {
        _isSeeking = false;
        var duration = DurationSeconds;
        _host.Player.Seek(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(duration, 0))));
        PositionSeconds = seconds;
        PositionText = DisplayFormatter.Duration(TimeSpan.FromSeconds(seconds));
    }

    partial void OnVolumeChanged(double value)
    {
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

    private async Task ScanLibraryAsync(bool silent)
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
            SetStatus("媒体库扫描完成");
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

        await _host.PlaylistRepository.CreatePlaylistAsync(name, PlaylistKind.Normal, null);
        await ReloadSourcesAsync();
        var node = Sources.LastOrDefault(s => s.Kind == SourceKind.Playlist && s.Playlist?.Name == name);
        if (node is not null)
        {
            await SelectSourceAsync(node);
        }

        SetStatus($"已创建播放列表：{name}");
    }

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
        row ??= SelectedRow;
        if (row is null)
        {
            return;
        }

        try
        {
            var data = await Task.Run(() => _host.TagService.Read(row.Track.Path));
            var edited = _dialogs.EditTags(row.Track, data);
            if (edited is null)
            {
                return;
            }

            var oldCover = row.Track.CoverKey;
            await Task.Run(() => _host.TagService.Write(row.Track.Path, edited));
            var updated = await _host.Scanner.IndexFileAsync(row.Track.Path, row.Track.IsInLibrary, row.Track.FolderId);
            if (updated is not null)
            {
                row.UpdateTrack(updated);
                _host.Covers?.Invalidate(oldCover);
                _host.Covers?.Invalidate(updated.CoverKey);
                _host.CoverImages.Invalidate(oldCover);
                _host.CoverImages.Invalidate(updated.CoverKey);
                await LoadRowCoverAsync(row, force: true);
                if (ReferenceEquals(CurrentRow, row))
                {
                    UpdateNowPlayingMetadata(row);
                    await LoadNowPlayingCoverAsync(updated);
                }
            }

            await ReloadSourcesAsync();
            SetStatus("标签已保存");
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"Tag edit failed for {row.Track.Path}", ex);
            _dialogs.Error($"保存标签失败：{ex.Message}");
        }
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
        var exe = Environment.ProcessPath;
        if (exe is null || !_host.Associations.Register(exe))
        {
            _dialogs.Error("注册为默认播放器失败（无法写入注册表）。");
            return;
        }

        SetStatus("已注册为音频文件候选播放器，可在 Windows 默认应用中设为默认");
        if (_dialogs.Confirm("注册成功。是否打开 Windows“默认应用”设置将 EchoGarage 设为默认播放器？"))
        {
            FileAssociationService.OpenDefaultAppsSettings();
        }
    }

    [RelayCommand]
    private void UnregisterDefaultPlayer()
    {
        if (_host.Associations.Unregister())
        {
            SetStatus("已取消文件关联注册");
        }
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
        _selectionGate.Dispose();
    }
}
