using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.App.ViewModels;

/// <summary>
/// Web 标签搜索窗口（ADR-022）的三步流程视图模型。
/// 窗口只负责拖拽、MessageBox 与 DialogResult；这里通过事件请求关闭 / 确认 / 提示。
/// </summary>
public sealed partial class WebTagSearchViewModel : ObservableObject
{
    public const string NoDataText = "无数据";

    private const int ResultThumbnailDecodeWidth = 240;
    private const int CoverPreviewDecodeWidth = 360;

    private readonly IReadOnlyList<Track> _allTracks;
    private readonly IWebTagService _webTagService;
    private readonly ISettingsService _settingsService;
    private readonly IAppLogger _logger;

    private readonly Dictionary<string, byte[]> _artworkCache = new(StringComparer.Ordinal);

    /// <summary>当前处理的一个专辑组（ADR-022：一次只处理一个专辑组）。</summary>
    private IReadOnlyList<Track> _tracks = [];

    private WebTagRelease? _release;
    private string? _searchStateKey;
    private string? _fieldRowsSourceId;
    private bool _step3Ready;
    private bool _updatingFieldRows;
    private bool _shutdown;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _releaseCts;
    private CancellationTokenSource? _coverCts;
    private Task? _coverLoadTask;

    private byte[]? _coverRaw;
    private string? _coverMime;
    private string? _coverError;

    public WebTagSearchViewModel(
        IReadOnlyList<Track> tracks,
        IWebTagService webTagService,
        ISettingsService settingsService,
        IAppLogger logger)
    {
        _allTracks = tracks ?? throw new ArgumentNullException(nameof(tracks));
        _tracks = _allTracks;
        _webTagService = webTagService ?? throw new ArgumentNullException(nameof(webTagService));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsResultsEmpty));
            RefreshCommands();
        };

        BuildInitialState();

        foreach (var source in _webTagService.EnabledSources)
        {
            Sources.Add(new WebTagSourceOption(source));
        }

        if (Sources.Count == 0)
        {
            foreach (var source in _webTagService.Sources)
            {
                Sources.Add(new WebTagSourceOption(source));
            }
        }

        var defaultId = _webTagService.DefaultSourceId;
        SelectedSource = Sources.FirstOrDefault(s => string.Equals(s.Id, defaultId, StringComparison.OrdinalIgnoreCase))
                         ?? Sources.FirstOrDefault();

        RefreshCommands();
    }

    public event EventHandler<WebTagMessageRequestedEventArgs>? MessageRequested;

    public event EventHandler<WebTagConfirmRequestedEventArgs>? ConfirmRequested;

    public event EventHandler<WebTagApplyRequestedEventArgs>? ApplyRequested;

    public event EventHandler? CancelRequested;

    #region 集合

    public ObservableCollection<WebTagSourceOption> Sources { get; } = [];

    /// <summary>选中歌曲按“专辑艺术家 + 专辑”分出的专辑组；一次只处理一个专辑组。</summary>
    public ObservableCollection<AlbumGroupOption> AlbumGroups { get; } = [];

    public ObservableCollection<WebTagSearchResultRow> Results { get; } = [];

    public ObservableCollection<RemoteTrackRow> RemoteTracks { get; } = [];

    public ObservableCollection<LocalFileRow> LocalFiles { get; } = [];

    /// <summary>左侧行内下拉兜底：第一项为“（未绑定）”。</summary>
    public ObservableCollection<LocalFileOption> LocalFileOptions { get; } = [];

    public ObservableCollection<WebTagFieldRow> TrackFields { get; } = [];

    public ObservableCollection<WebTagFieldRow> AlbumFields { get; } = [];

    #endregion

    #region 状态属性

    [ObservableProperty]
    private int _step = 1;

    [ObservableProperty]
    private string _keyword = string.Empty;

    [ObservableProperty]
    private WebTagSourceOption? _selectedSource;

    [ObservableProperty]
    private string _groupSummary = string.Empty;

    /// <summary>当前要搜索的专辑组（ADR-022：一次只处理一个专辑组）。</summary>
    [ObservableProperty]
    private AlbumGroupOption? _selectedAlbumGroup;

    /// <summary>仅一个专辑组时隐藏分组下拉。</summary>
    public bool HasMultipleAlbumGroups => AlbumGroups.Count > 1;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _hasSearchCompleted;

    [ObservableProperty]
    private string _searchSummary = string.Empty;

    [ObservableProperty]
    private WebTagSearchResultRow? _selectedResult;

    [ObservableProperty]
    private bool _isManualLinkOpen;

    [ObservableProperty]
    private string _manualLinkInput = string.Empty;

    [ObservableProperty]
    private string _manualLinkHint = "支持粘贴专辑链接或专辑 ID，识别后直接进入匹配步骤。";

    [ObservableProperty]
    private string _releaseSummary = string.Empty;

    [ObservableProperty]
    private RemoteTrackRow? _selectedRemoteRow;

    [ObservableProperty]
    private WebTagFieldRow? _coverField;

    [ObservableProperty]
    private ImageSource? _coverImage;

    [ObservableProperty]
    private string _coverStatus = string.Empty;

    [ObservableProperty]
    private bool _isCoverLoading;

    [ObservableProperty]
    private bool _convertCoverEnabled = true;

    [ObservableProperty]
    private string _coverQualityText = "90";

    [ObservableProperty]
    private string _coverMaxEdgeText = "1000";

    [ObservableProperty]
    private string _writeHint = string.Empty;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    public bool IsStep3 => Step == 3;

    /// <summary>底部只在「暂不可写入」时提示原因；常规状态下不占用空间。</summary>
    public bool HasWriteHint => WriteHint.Length > 0;

    public bool IsResultsEmpty => HasSearchCompleted && Results.Count == 0;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasSelectedFields => AlbumFields.Concat(TrackFields).Any(row => row.IsSelected);

    public bool HasWritableFiles => LocalFiles.Any(file => file.IsWritable);

    public bool HasAnyMatch => RemoteTracks.Any(row => row.IsBound);

    #endregion

    #region 命令

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        var source = SelectedSource;
        if (source is null)
        {
            ShowMessage("当前没有可用的 Web 标签源。", "Web 标签搜索", isError: true);
            return;
        }

        var term = Keyword.Trim();
        if (term.Length == 0)
        {
            return;
        }

        var cacheKey = source.Id + "\u0001" + term;
        if (HasSearchCompleted && string.Equals(_searchStateKey, cacheKey, StringComparison.Ordinal))
        {
            // 上一步返回后再次搜索同一关键词：不清空上次结果，也不重复联网。
            SetStep(2);
            return;
        }

        CancelSearchOps();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsBusy = true;
        BusyText = "正在搜索…";
        try
        {
            var items = await _webTagService.SearchAsync(source.Id, term, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Results.Clear();
            foreach (var item in items)
            {
                Results.Add(new WebTagSearchResultRow(item));
            }

            SelectedResult = Results.FirstOrDefault();
            HasSearchCompleted = true;
            _searchStateKey = cacheKey;
            // 正常搜索但结果为空时保持空输出，不额外提示；网络/解析错误已在 catch 中弹框。
            SearchSummary = Results.Count == 0
                ? string.Empty
                : $"关键词“{term}” · 共 {Results.Count} 个结果";
            StatusMessage = string.Empty;
            SetStep(2);
            _ = LoadThumbnailsAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 用户关闭窗口或切换步骤，忽略。
        }
        catch (Exception ex)
        {
            _logger.Error($"Web 标签搜索失败（{source.DisplayName}）：{ex.Message}", ex);
            ShowMessage($"搜索失败：{ex.Message}\n请检查网络 / 代理设置后重试。", "Web 标签搜索", isError: true);
        }
        finally
        {
            // 搜索期间若用户切换来源 / 关闭窗口，cts 已被 CancelSearchOps 取消并置空；
            // 这里仍要负责收尾忙碌状态，避免界面永久卡在“正在搜索”。
            if (ReferenceEquals(_searchCts, cts) || cts.IsCancellationRequested)
            {
                if (ReferenceEquals(_searchCts, cts))
                {
                    _searchCts = null;
                }

                IsBusy = false;
                BusyText = string.Empty;
            }
        }
    }

    private bool CanSearch()
        => !IsBusy && Step == 1 && SelectedSource is not null && !string.IsNullOrWhiteSpace(Keyword);

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        if (Step == 2)
        {
            SetStep(1);
            return;
        }

        if (Step == 3)
        {
            SetStep(2);
            RestartThumbnailLoadIfNeeded();
        }
    }

    private bool CanBack() => !IsBusy && (Step == 2 || Step == 3);

    [RelayCommand(CanExecute = nameof(CanGoToMatch))]
    private async Task GoToMatchAsync()
    {
        await OpenReleaseAsync(SelectedResult);
    }

    private bool CanGoToMatch() => !IsBusy && Step == 2 && SelectedResult is not null;

    [RelayCommand(CanExecute = nameof(CanRetryThumbnails))]
    private void RetryThumbnails()
    {
        StatusMessage = string.Empty;
        RestartThumbnailLoadIfNeeded();
    }

    private bool CanRetryThumbnails()
        => !IsBusy
           && Step == 2
           && Results.Any(r => r.Thumbnail is null && !string.IsNullOrWhiteSpace(r.Item.ArtworkUrl));

    [RelayCommand(CanExecute = nameof(CanToggleManualLink))]
    private void ToggleManualLink() => IsManualLinkOpen = !IsManualLinkOpen;

    private bool CanToggleManualLink() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUseManualLink))]
    private async Task UseManualLinkAsync()
    {
        var source = SelectedSource;
        if (source is null || string.IsNullOrWhiteSpace(ManualLinkInput))
        {
            return;
        }

        var input = ManualLinkInput.Trim();
        if (!_webTagService.TryParseReleaseId(source.Id, input, out var releaseId))
        {
            ManualLinkHint = "无法识别输入，请粘贴专辑链接或专辑 ID。";
            return;
        }

        CancelReleaseOps();
        var cts = new CancellationTokenSource();
        _releaseCts = cts;
        IsBusy = true;
        BusyText = "正在加载专辑详情…";
        try
        {
            var release = await _webTagService.GetReleaseAsync(source.Id, releaseId, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (release is null)
            {
                ManualLinkHint = "该链接没有返回专辑详情，请检查后重试。";
                return;
            }

            var year = release.AlbumFields.TryGetValue(WebTagFieldKeys.Year, out var yearText)
                       && int.TryParse(yearText, out var parsedYear)
                ? parsedYear
                : (int?)null;
            var item = new WebTagSearchItem
            {
                SourceId = release.SourceId,
                ReleaseId = release.ReleaseId,
                Title = release.Title,
                Artist = release.Artist,
                TrackCount = release.Tracks.Count,
                Year = year,
                ArtworkUrl = release.ArtworkUrl,
                SourceUrl = release.SourceUrl,
                ResultKind = WebTagResultKind.Album
            };

            var row = new WebTagSearchResultRow(item);
            Results.Insert(0, row);
            SelectedResult = row;
            HasSearchCompleted = true;
            _searchStateKey = null;
            SearchSummary = "已通过手动链接加载专辑";
            ManualLinkHint = "已识别链接并加载专辑。";
            IsManualLinkOpen = false;

            InitializeStep3(release);
            SetStep(3);
            MaybeLoadCover();
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
        catch (Exception ex)
        {
            _logger.Error($"通过手动链接加载 Web 标签专辑失败：{ex.Message}", ex);
            ManualLinkHint = $"加载失败：{ex.Message}";
            ShowMessage($"加载专辑详情失败：{ex.Message}\n请检查链接或网络后重试。", "Web 标签搜索", isError: true);
        }
        finally
        {
            if (ReferenceEquals(_releaseCts, cts) || cts.IsCancellationRequested)
            {
                if (ReferenceEquals(_releaseCts, cts))
                {
                    _releaseCts = null;
                }

                IsBusy = false;
                BusyText = string.Empty;
            }
        }
    }

    private bool CanUseManualLink()
        => !IsBusy && SelectedSource is not null && !string.IsNullOrWhiteSpace(ManualLinkInput);

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task WriteAsync()
    {
        var release = _release;
        if (Step != 3 || release is null || !_step3Ready)
        {
            return;
        }

        IsBusy = true;
        BusyText = "正在生成写入计划…";
        try
        {
            // 若用户勾选了封面但还在下载，先等下载结束（最多等网络层超时）。
            if (CoverField is { IsSelected: true } && _coverRaw is null && _coverLoadTask is not null)
            {
                BusyText = "正在等待封面下载…";
                try
                {
                    await _coverLoadTask;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"等待封面下载时出现异常：{ex.Message}");
                }

                if (!ReferenceEquals(release, _release))
                {
                    return;
                }
            }

            if (_shutdown)
            {
                // 用户已在等待封面期间关闭窗口，不再提示 / 写入。
                return;
            }

            if (CoverField is { IsSelected: true } && _coverRaw is { Length: > 0 } rawCover
                && CoverImageProcessor.ReadInfo(rawCover) is null)
            {
                ShowMessage("封面图片无法解码，本次不会写入封面。", "Web 标签", isError: false);
            }

            var coverSelected = CoverField is { IsSelected: true };
            var coverRaw = _coverRaw;
            var coverMime = _coverMime;
            var convertCover = ConvertCoverEnabled;
            var quality = ParseIntInRange(CoverQualityText, 1, 100, 90);
            var maxEdge = ParseIntInRange(CoverMaxEdgeText, 0, 20000, 1000);
            var cover = coverSelected
                ? await Task.Run(() => BuildCover(coverRaw, coverMime, convertCover, quality, maxEdge))
                : null;
            var albumFields = new HashSet<string>(
                AlbumFields.Where(row => row.IsSelected).Select(row => row.Key),
                StringComparer.OrdinalIgnoreCase);
            var trackFields = new HashSet<string>(
                TrackFields.Where(row => row.IsSelected).Select(row => row.Key),
                StringComparer.OrdinalIgnoreCase);

            // 先保存字段勾选记忆，再生成计划，保证下次打开窗口沿用本次勾选。
            _settingsService.Current.WebTags.SetSelectedFields(release.SourceId, SelectedFieldKeys());
            BusyText = "正在保存字段勾选…";
            await _settingsService.SaveAsync();

            if (_shutdown)
            {
                return;
            }

            var plan = WebTagApplyPlanner.BuildPlan(
                BuildMatches(),
                release,
                albumFields,
                trackFields,
                includeCover: cover is not null,
                cover);

            if (plan.Edits.Count == 0)
            {
                ShowMessage("没有可写入的内容：所选字段为空，或远程数据没有对应值。", "Web 标签", isError: false);
                return;
            }

            if (_shutdown)
            {
                return;
            }

            ApplyRequested?.Invoke(this, new WebTagApplyRequestedEventArgs(plan));
        }
        catch (Exception ex)
        {
            _logger.Error("生成 Web 标签写入计划失败。", ex);
            ShowMessage($"生成写入计划失败：{ex.Message}", "Web 标签", isError: true);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    private bool CanWrite()
        => !IsBusy && Step == 3 && _step3Ready && HasSelectedFields && HasWritableFiles;

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanReAutoMatch))]
    private void ReAutoMatch()
    {
        if (_release is null)
        {
            return;
        }

        if (RemoteTracks.Any(row => row.MatchKind == WebTagMatchKind.Manual)
            && !Confirm("重新自动匹配会丢弃全部手动绑定，确定继续吗？", "重新自动匹配"))
        {
            return;
        }

        ClearAllBindingsCore();
        foreach (var match in WebTagMatcher.Match(_tracks, _release))
        {
            var local = FindLocalRow(match.LocalTrack);
            var remote = FindRemoteRow(match.RemoteTrack);
            if (local is not null && remote is not null)
            {
                BindRemoteCore(remote, local, match.Kind);
            }
        }

        StatusMessage = "已按自动匹配规则重新匹配。";
        SelectedRemoteRow = RemoteTracks.FirstOrDefault(row => row.IsBound) ?? RemoteTracks.FirstOrDefault();
        RefreshAfterBindingChange();
    }

    private bool CanReAutoMatch() => !IsBusy && Step == 3 && _step3Ready && RemoteTracks.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearMatches))]
    private void ClearMatches()
    {
        if (!Confirm("确定清空全部本地文件与远程曲目的匹配关系吗？", "清空匹配"))
        {
            return;
        }

        ClearAllBindingsCore();
        StatusMessage = "已清空全部匹配，可重新拖拽绑定。";
        RefreshAfterBindingChange();
    }

    private bool CanClearMatches() => !IsBusy && Step == 3 && _step3Ready && HasAnyMatch;

    [RelayCommand(CanExecute = nameof(CanRetryCover))]
    private void RetryCover()
    {
        _coverError = null;
        StartCoverLoad();
    }

    private bool CanRetryCover()
        => !IsBusy
           && Step == 3
           && !IsCoverLoading
           && _release is not null
           && !string.IsNullOrWhiteSpace(_release.ArtworkUrl);

    [RelayCommand(CanExecute = nameof(CanUnbindRemote))]
    private void UnbindRemote(RemoteTrackRow? row)
    {
        if (row is null || !row.IsBound)
        {
            return;
        }

        UnbindRemoteState(row);
        StatusMessage = $"已解除远程曲目 {row.OrdinalDisplay} 的绑定。";
        RefreshAfterBindingChange();
    }

    private bool CanUnbindRemote(RemoteTrackRow? row) => !IsBusy && row is { IsBound: true };

    #endregion

    #region 对外操作（拖拽 / 关闭）

    /// <summary>把右侧本地文件显式绑定到左侧远程曲目（拖拽或行内下拉）。</summary>
    public void BindLocalToRemote(RemoteTrackRow remote, LocalFileRow local)
    {
        if (Step != 3 || !_step3Ready)
        {
            return;
        }

        BindRemote(remote, local, WebTagMatchKind.Manual);
    }

    /// <summary>把本地文件拖到右侧空白处：解除该文件的绑定。</summary>
    public void UnbindLocalFile(LocalFileRow file)
    {
        if (Step != 3 || !_step3Ready)
        {
            return;
        }

        if (file.BoundRemote is not { } remote)
        {
            StatusMessage = "该本地文件当前没有绑定远程曲目。";
            return;
        }

        UnbindRemoteState(remote);
        StatusMessage = $"已解除本地文件“{file.TitleDisplay}”的绑定。";
        RefreshAfterBindingChange();
    }

    public void SetDragOverRow(RemoteTrackRow? row)
    {
        foreach (var item in RemoteTracks)
        {
            if (!ReferenceEquals(item, row) && item.IsDragOver)
            {
                item.IsDragOver = false;
            }
        }

        if (row is not null && !row.IsDragOver)
        {
            row.IsDragOver = true;
        }
    }

    public void NotifyRemoteDropTargetMissing()
        => StatusMessage = "请把本地文件拖到左侧某一首远程曲目上。";

    public void NotifyLocalDropTargetMissing()
        => StatusMessage = "若要解除绑定，请把本地文件拖到右侧列表的空白区域。";

    /// <summary>关闭窗口时保存字段勾选（fire-and-forget，调用方无需等待）。</summary>
    public async Task SaveFieldSelectionAsync()
    {
        var sourceId = _fieldRowsSourceId;
        if (string.IsNullOrEmpty(sourceId))
        {
            return;
        }

        try
        {
            _settingsService.Current.WebTags.SetSelectedFields(sourceId, SelectedFieldKeys());
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.Warn($"保存 Web 标签字段勾选失败：{ex.Message}");
        }
    }

    public void Shutdown()
    {
        _shutdown = true;
        CancelSearchOps();
        CancelReleaseOps();
        CancelCoverOps();
    }

    public IReadOnlyList<string> SelectedFieldKeys()
        => AlbumFields.Concat(TrackFields).Where(row => row.IsSelected).Select(row => row.Key).ToList();

    public static ImageSource? CreateImage(byte[]? bytes, int decodePixelWidth)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    #endregion

    #region 初始化

    private void BuildInitialState()
    {
        AlbumGroups.Clear();

        if (_allTracks.Count == 0)
        {
            _tracks = [];
            GroupSummary = "未选择歌曲";
            Keyword = string.Empty;
            OnPropertyChanged(nameof(HasMultipleAlbumGroups));
            return;
        }

        // ADR-022：按“专辑艺术家 + 专辑名”分组，缺少专辑名的歌按所在目录分组；
        // 一次只处理一个专辑组，第一阶段不做多专辑排队。
        var groups = _allTracks
            .GroupBy(MakeGroupKey)
            .Select(group => group.ToList())
            .Select(groupTracks => new AlbumGroupOption(groupTracks, FormatGroup(groupTracks)))
            .Select((group, index) => (Group: group, Index: index))
            .OrderByDescending(item => item.Group.Tracks.Count)
            .ThenBy(item => item.Index)
            .Select(item => item.Group)
            .ToList();

        foreach (var group in groups)
        {
            AlbumGroups.Add(group);
        }

        OnPropertyChanged(nameof(HasMultipleAlbumGroups));

        // 默认选中第一组（曲目最多，数量相同按原选中顺序）；赋值会触发 ApplyAlbumGroup。
        SelectedAlbumGroup = AlbumGroups[0];
    }

    private static string MakeGroupKey(Track track)
    {
        var artist = Clean(track.AlbumArtist);
        if (artist.Length == 0)
        {
            artist = Clean(track.Artist);
        }

        var album = Clean(track.Album);
        var fromDirectory = album.Length == 0;
        var albumKey = fromDirectory
            ? (Path.GetDirectoryName(track.Path) ?? string.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : album;

        // 目录名不区分大小写（Windows 语义），缺失专辑名的歌不同目录必须是不同组。
        return $"{artist.ToUpperInvariant()}\u0001{(fromDirectory ? 'd' : 'a')}\u0001{albumKey.ToUpperInvariant()}";
    }

    private static string FormatGroup(IReadOnlyList<Track> tracks)
    {
        var first = tracks[0];
        var artist = Clean(first.AlbumArtist);
        if (artist.Length == 0)
        {
            artist = Clean(first.Artist);
        }

        if (artist.Length == 0)
        {
            artist = "未知艺术家";
        }

        var album = Clean(first.Album);
        if (album.Length > 0)
        {
            return $"{artist} — {album}（{tracks.Count} 首）";
        }

        var directory = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(first.Path) ?? string.Empty));
        if (directory.Length > 0)
        {
            return $"{artist} — 目录「{directory}」（{tracks.Count} 首）";
        }

        return $"{artist} — 未知专辑（{tracks.Count} 首）";
    }

    private static string BuildKeyword(Track track)
    {
        var albumArtist = Clean(track.AlbumArtist);
        var artist = Clean(track.Artist);
        var album = Clean(track.Album);
        var title = Clean(track.Title);

        if (albumArtist.Length > 0 && album.Length > 0)
        {
            return $"{albumArtist} {album}";
        }

        if (artist.Length > 0 && album.Length > 0)
        {
            return $"{artist} {album}";
        }

        if (artist.Length > 0 && title.Length > 0)
        {
            return $"{artist} {title}";
        }

        if (album.Length > 0)
        {
            return album;
        }

        if (title.Length > 0)
        {
            return title;
        }

        return Clean(track.DisplayTitle);
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    #endregion

    #region 第二步：加载详情 / 缩略图

    private async Task OpenReleaseAsync(WebTagSearchResultRow? row)
    {
        if (row is null)
        {
            return;
        }

        // 同一张专辑再次进入第三步：复用已加载的 release 与匹配状态，不重复联网。
        if (_step3Ready
            && _release is not null
            && string.Equals(_release.SourceId, row.Item.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_release.ReleaseId, row.Item.ReleaseId, StringComparison.Ordinal))
        {
            SetStep(3);
            MaybeLoadCover();
            return;
        }

        CancelReleaseOps();
        CancelCoverOps();
        var cts = new CancellationTokenSource();
        _releaseCts = cts;
        IsBusy = true;
        BusyText = "正在加载专辑详情…";
        try
        {
            var release = await _webTagService.GetReleaseAsync(row.Item.SourceId, row.Item.ReleaseId, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (release is null)
            {
                ShowMessage("没有获取到专辑详情，请返回上一步重新选择。", "Web 标签搜索", isError: true);
                return;
            }

            InitializeStep3(release);
            SetStep(3);
            MaybeLoadCover();
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
        catch (Exception ex)
        {
            _logger.Error($"加载 Web 标签专辑详情失败：{ex.Message}", ex);
            ShowMessage($"加载专辑详情失败：{ex.Message}\n可点击“下一步”重试。", "Web 标签搜索", isError: true);
        }
        finally
        {
            if (ReferenceEquals(_releaseCts, cts) || cts.IsCancellationRequested)
            {
                if (ReferenceEquals(_releaseCts, cts))
                {
                    _releaseCts = null;
                }

                IsBusy = false;
                BusyText = string.Empty;
            }
        }
    }

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        foreach (var row in Results.ToList())
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var url = row.Item.ArtworkUrl;
            if (row.Thumbnail is not null || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            try
            {
                var bytes = await GetArtworkBytesAsync(url, token);
                if (bytes is null || token.IsCancellationRequested)
                {
                    continue;
                }

                var image = CreateImage(bytes, ResultThumbnailDecodeWidth);
                if (image is not null)
                {
                    row.Thumbnail = image;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        RefreshCommands();
    }

    private async Task<byte[]?> GetArtworkBytesAsync(string url, CancellationToken token)
    {
        if (_artworkCache.TryGetValue(url, out var cached))
        {
            return cached;
        }

        try
        {
            var cover = await _webTagService.DownloadCoverAsync(url, token);
            if (cover.Bytes.Length == 0)
            {
                return null;
            }

            _artworkCache[url] = cover.Bytes;
            return cover.Bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug($"Web 标签搜索结果封面下载失败：{ex.Message}");
            return null;
        }
    }

    private void RestartThumbnailLoadIfNeeded()
    {
        if (!Results.Any(r => r.Thumbnail is null && !string.IsNullOrWhiteSpace(r.Item.ArtworkUrl)))
        {
            return;
        }

        CancelSearchOps();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        _ = LoadThumbnailsAsync(cts.Token);
    }

    private void CancelSearchOps()
    {
        var cts = _searchCts;
        _searchCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略。
        }
    }

    private void CancelReleaseOps()
    {
        var cts = _releaseCts;
        _releaseCts = null;
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 忽略。
        }
    }

    #endregion

    #region 第三步：匹配与预览

    private void InitializeStep3(WebTagRelease release)
    {
        _step3Ready = false;
        _release = release;

        CancelCoverOps();
        _coverRaw = null;
        _coverMime = null;
        _coverError = null;
        CoverImage = null;
        CoverStatus = "未下载";

        EnsureFieldRows(release);

        ReleaseSummary = string.IsNullOrWhiteSpace(release.DisplayArtist) || release.DisplayArtist == "—"
            ? $"《{release.DisplayTitle}》"
            : $"{release.DisplayArtist} — 《{release.DisplayTitle}》";

        RemoteTracks.Clear();
        for (var index = 0; index < release.Tracks.Count; index++)
        {
            var row = new RemoteTrackRow(release.Tracks[index], index)
            {
                OptionChanged = OnRemoteOptionChanged
            };
            RemoteTracks.Add(row);
        }

        LocalFiles.Clear();
        foreach (var track in _tracks)
        {
            LocalFiles.Add(new LocalFileRow(track, LocalFiles.Count));
        }

        RebuildLocalFileOptions();

        // 自动匹配：ADR-022 规定绝不按顺序强配。
        foreach (var match in WebTagMatcher.Match(_tracks, release))
        {
            var local = FindLocalRow(match.LocalTrack);
            var remote = FindRemoteRow(match.RemoteTrack);
            if (local is not null && remote is not null)
            {
                BindRemoteCore(remote, local, match.Kind);
            }
        }

        SelectedRemoteRow = RemoteTracks.FirstOrDefault(row => row.IsBound) ?? RemoteTracks.FirstOrDefault();
        UpdateAlbumFields();
        UpdateTrackFields();
        SelectedResult = Results.FirstOrDefault(r =>
            string.Equals(r.Item.SourceId, release.SourceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.Item.ReleaseId, release.ReleaseId, StringComparison.Ordinal)) ?? SelectedResult;

        _step3Ready = true;
        StatusMessage = string.Empty;
        RefreshAfterBindingChange();
    }

    private void EnsureFieldRows(WebTagRelease release)
    {
        var sourceId = release.SourceId;
        if (string.Equals(_fieldRowsSourceId, sourceId, StringComparison.OrdinalIgnoreCase) && AlbumFields.Count > 0)
        {
            return;
        }

        _fieldRowsSourceId = sourceId;
        var remembered = new HashSet<string>(
            _settingsService.Current.WebTags.GetSelectedFields(sourceId),
            StringComparer.OrdinalIgnoreCase);

        _updatingFieldRows = true;
        try
        {
            foreach (var row in AlbumFields.Concat(TrackFields))
            {
                row.PropertyChanged -= OnFieldRowPropertyChanged;
            }

            AlbumFields.Clear();
            TrackFields.Clear();

            foreach (var definition in WebTagFieldCatalog.All)
            {
                var row = new WebTagFieldRow(definition.Key, definition.DisplayName)
                {
                    IsSelected = remembered.Contains(definition.Key),
                    IsInteractive = false
                };
                row.PropertyChanged += OnFieldRowPropertyChanged;
                if (definition.IsAlbumField)
                {
                    AlbumFields.Add(row);
                }
                else
                {
                    TrackFields.Add(row);
                }
            }

            CoverField = AlbumFields.FirstOrDefault(row => row.Key == WebTagFieldKeys.Cover);
            if (CoverField is not null)
            {
                // 封面即使暂时没有数据也允许取消勾选（ADR-022 允许下载失败后取消封面）。
                CoverField.IsInteractive = true;
            }
        }
        finally
        {
            _updatingFieldRows = false;
        }
    }

    private void OnFieldRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WebTagFieldRow.IsSelected) || _updatingFieldRows)
        {
            return;
        }

        RefreshWriteState();
        RefreshCommands();
    }

    private void UpdateTrackFields()
    {
        var track = SelectedRemoteRow?.Track;
        foreach (var row in TrackFields)
        {
            var hasValue = track is not null
                           && track.Fields.TryGetValue(row.Key, out var value)
                           && !string.IsNullOrWhiteSpace(value);
            row.DisplayValue = hasValue ? track!.Fields[row.Key].Trim() : NoDataText;
            row.HasValue = hasValue;
        }
    }

    private void UpdateAlbumFields()
    {
        var release = _release;
        foreach (var row in AlbumFields)
        {
            if (ReferenceEquals(row, CoverField))
            {
                continue;
            }

            var hasValue = release is not null
                           && release.AlbumFields.TryGetValue(row.Key, out var value)
                           && !string.IsNullOrWhiteSpace(value);
            row.DisplayValue = hasValue ? release!.AlbumFields[row.Key].Trim() : NoDataText;
            row.HasValue = hasValue;
        }

        RefreshCoverState();
    }

    private void OnRemoteOptionChanged(RemoteTrackRow remote, LocalFileRow? file)
    {
        if (!_step3Ready)
        {
            return;
        }

        if (file is null)
        {
            if (!remote.IsBound)
            {
                return;
            }

            UnbindRemoteState(remote);
            StatusMessage = $"已解除远程曲目 {remote.OrdinalDisplay} 的绑定。";
            RefreshAfterBindingChange();
            return;
        }

        if (ReferenceEquals(remote.BoundLocal, file))
        {
            return;
        }

        BindRemote(remote, file, WebTagMatchKind.Manual);
    }

    private void BindRemote(RemoteTrackRow remote, LocalFileRow local, WebTagMatchKind kind)
    {
        BindRemoteCore(remote, local, kind);
        StatusMessage = $"已将“{local.TitleDisplay}”绑定到远程曲目 {remote.OrdinalDisplay} {remote.TitleDisplay}。";
        RefreshAfterBindingChange();
    }

    private void BindRemoteCore(RemoteTrackRow remote, LocalFileRow local, WebTagMatchKind kind)
    {
        if (remote.BoundLocal is { } previous && !ReferenceEquals(previous, local))
        {
            previous.BoundRemote = null;
            previous.MatchStatus = "未匹配";
        }

        if (local.BoundRemote is { } previousRemote && !ReferenceEquals(previousRemote, remote))
        {
            UnbindRemoteState(previousRemote);
        }

        remote.BoundLocal = local;
        remote.MatchKind = kind;
        SetRemoteOption(remote, local);
        local.BoundRemote = remote;
        local.MatchStatus = kind == WebTagMatchKind.Manual ? "手动绑定" : "自动匹配";
    }

    private void UnbindRemoteState(RemoteTrackRow remote)
    {
        if (remote.BoundLocal is { } local)
        {
            local.BoundRemote = null;
            local.MatchStatus = "未匹配";
        }

        remote.BoundLocal = null;
        remote.MatchKind = WebTagMatchKind.None;
        SetRemoteOption(remote, null);
    }

    private void ClearAllBindingsCore()
    {
        foreach (var row in RemoteTracks)
        {
            UnbindRemoteState(row);
        }
    }

    private void RebuildLocalFileOptions()
    {
        LocalFileOptions.Clear();
        LocalFileOptions.Add(new LocalFileOption(null, "（未绑定）"));
        foreach (var file in LocalFiles)
        {
            LocalFileOptions.Add(new LocalFileOption(file, file.TitleDisplay));
        }

        foreach (var remote in RemoteTracks)
        {
            SetRemoteOption(remote, remote.BoundLocal);
        }
    }

    private void SetRemoteOption(RemoteTrackRow remote, LocalFileRow? file)
    {
        var option = LocalFileOptions.FirstOrDefault(item => ReferenceEquals(item.File, file))
                     ?? LocalFileOptions.FirstOrDefault();
        remote.SetSelectedOptionSilently(option);
    }

    private RemoteTrackRow? FindRemoteRow(WebTagTrack? track)
        => track is null ? null : RemoteTracks.FirstOrDefault(row => ReferenceEquals(row.Track, track));

    private LocalFileRow? FindLocalRow(Track? track)
        => track is null ? null : LocalFiles.FirstOrDefault(row => ReferenceEquals(row.Track, track));

    private void RefreshAfterBindingChange()
    {
        RefreshWriteState();
        RefreshCommands();
        OnPropertyChanged(nameof(HasAnyMatch));
    }

    private void RefreshWriteState()
    {
        var reasons = new List<string>();
        if (!HasSelectedFields)
        {
            reasons.Add("至少勾选一个要写入的字段");
        }

        if (!HasWritableFiles)
        {
            reasons.Add("至少需要一个可写的本地文件");
        }

        WriteHint = reasons.Count == 0
            ? string.Empty
            : $"暂不可写入：{string.Join("；", reasons)}。";
        OnPropertyChanged(nameof(HasWriteHint));
        WriteCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedFields));
        OnPropertyChanged(nameof(HasWritableFiles));
    }

    #endregion

    #region 封面

    private void MaybeLoadCover()
    {
        if (_release is null || Step != 3)
        {
            return;
        }

        if (_coverRaw is not null || _coverError is not null || IsCoverLoading)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_release.ArtworkUrl))
        {
            _coverError = "该来源没有提供封面。";
            RefreshCoverState();
            return;
        }

        StartCoverLoad();
    }

    private void StartCoverLoad()
    {
        if (_release is null || string.IsNullOrWhiteSpace(_release.ArtworkUrl))
        {
            return;
        }

        CancelCoverOps();
        var cts = new CancellationTokenSource();
        _coverCts = cts;
        _coverError = null;
        IsCoverLoading = true;
        RefreshCoverState();
        _coverLoadTask = LoadCoverAsync(_release.ArtworkUrl!, cts);
    }

    private async Task LoadCoverAsync(string url, CancellationTokenSource cts)
    {
        try
        {
            var cover = await _webTagService.DownloadCoverAsync(url, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (cover.Bytes.Length == 0)
            {
                _coverError = "封面下载失败：返回内容为空。";
                return;
            }

            _coverRaw = cover.Bytes;
            _coverMime = cover.Mime;
            CoverImage = CreateImage(cover.Bytes, CoverPreviewDecodeWidth);
        }
        catch (OperationCanceledException)
        {
            // 忽略。
        }
        catch (Exception ex)
        {
            _coverError = $"封面下载失败：{ex.Message}";
            _logger.Warn($"Web 标签封面下载失败：{ex.Message}");
            ShowMessage(
                $"{_coverError}\n可点击“重试”重新下载，或取消勾选“封面”后只写入文本字段。",
                "Web 标签",
                isError: false);
        }
        finally
        {
            if (ReferenceEquals(_coverCts, cts))
            {
                _coverCts = null;
                _coverLoadTask = null;
                IsCoverLoading = false;
                RefreshCoverState();
            }
        }
    }

    private void CancelCoverOps()
    {
        var cts = _coverCts;
        _coverCts = null;
        _coverLoadTask = null;
        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 忽略。
            }
        }

        if (IsCoverLoading)
        {
            IsCoverLoading = false;
        }
    }

    private void RefreshCoverState()
    {
        string status;
        if (_release is null)
        {
            status = "未加载";
        }
        else if (IsCoverLoading)
        {
            status = "正在下载封面…";
        }
        else if (_coverRaw is { Length: > 0 })
        {
            var info = CoverImageProcessor.ReadInfo(_coverRaw);
            status = info is null
                ? "封面已下载（无法解析图片信息）"
                : $"封面已就绪：{info.FormatName} {info.Width}×{info.Height} · {DisplayFormatter.FileSize(info.ByteSize)}";
        }
        else if (_coverError is not null)
        {
            status = _coverError;
        }
        else if (_release is not null && string.IsNullOrWhiteSpace(_release.ArtworkUrl))
        {
            status = "该来源没有提供封面。";
        }
        else
        {
            status = "封面未下载。";
        }

        CoverStatus = status;
        if (CoverField is not null)
        {
            CoverField.HasValue = _coverRaw is { Length: > 0 };
            CoverField.DisplayValue = _coverRaw is { Length: > 0 } ? "已就绪" : NoDataText;
        }

        RetryCoverCommand.NotifyCanExecuteChanged();
    }

    private static WebTagCover? BuildCover(
        byte[]? raw,
        string? coverMime,
        bool convertCover,
        int quality,
        int maxEdge)
    {
        if (raw is not { Length: > 0 })
        {
            return null;
        }

        if (CoverImageProcessor.ReadInfo(raw) is null)
        {
            return null;
        }

        var originalMime = string.IsNullOrWhiteSpace(coverMime) ? "image/jpeg" : coverMime;
        if (!convertCover)
        {
            return new WebTagCover(raw, originalMime);
        }

        var converted = CoverImageProcessor.ConvertToJpeg(raw, quality, maxEdge);
        if (converted.Bytes.Length == 0)
        {
            return new WebTagCover(raw, originalMime);
        }

        return new WebTagCover(converted.Bytes, converted.Converted ? CoverImageProcessor.JpegMime : originalMime);
    }

    private static int ParseIntInRange(string? text, int min, int max, int fallback)
        => int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value >= min
           && value <= max
            ? value
            : fallback;

    #endregion

    #region 写入计划

    private IReadOnlyList<WebTagMatch> BuildMatches()
    {
        var matches = new List<WebTagMatch>(LocalFiles.Count);
        foreach (var file in LocalFiles)
        {
            var remote = file.BoundRemote;
            var includeTrackFields = remote is not null && remote.IsWriteEnabled;
            matches.Add(new WebTagMatch(
                file.Track,
                includeTrackFields ? remote!.Track : null,
                includeTrackFields ? remote!.MatchKind : WebTagMatchKind.None));
        }

        return matches;
    }

    #endregion

    #region 步骤 / 提示

    private void SetStep(int step)
    {
        if (Step == step)
        {
            return;
        }

        var previous = Step;
        if (previous == 2)
        {
            CancelSearchOps();
        }

        if (previous == 3)
        {
            CancelCoverOps();
        }

        Step = step;
        if (step == 1)
        {
            StatusMessage = string.Empty;
        }
    }

    partial void OnStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1));
        OnPropertyChanged(nameof(IsStep2));
        OnPropertyChanged(nameof(IsStep3));
        RefreshCommands();
    }

    partial void OnSelectedAlbumGroupChanged(AlbumGroupOption? value)
    {
        if (value is null)
        {
            return;
        }

        ApplyAlbumGroup(value);
    }

    /// <summary>切换“要搜索的专辑”后，本地文件、关键词与搜索状态全部重置为该专辑组。</summary>
    private void ApplyAlbumGroup(AlbumGroupOption group)
    {
        _tracks = group.Tracks;
        GroupSummary = group.Display;
        Keyword = group.Tracks.Count > 0 ? BuildKeyword(group.Tracks[0]) : string.Empty;

        CancelSearchOps();
        _searchStateKey = null;
        HasSearchCompleted = false;
        Results.Clear();
        SelectedResult = null;
        SearchSummary = string.Empty;
        StatusMessage = string.Empty;
        _step3Ready = false;
        RefreshCommands();
    }

    partial void OnSelectedSourceChanged(WebTagSourceOption? value)
    {
        CancelSearchOps();
        _fieldRowsSourceId = null;
        _searchStateKey = null;
        HasSearchCompleted = false;
        Results.Clear();
        SelectedResult = null;
        SearchSummary = string.Empty;
        StatusMessage = string.Empty;
        RefreshCommands();
    }

    partial void OnKeywordChanged(string value) => RefreshCommands();

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnManualLinkInputChanged(string value)
    {
        ManualLinkHint = "支持粘贴专辑链接或专辑 ID，识别后直接进入匹配步骤。";
        RefreshCommands();
    }

    partial void OnIsManualLinkOpenChanged(bool value) => RefreshCommands();

    partial void OnSelectedResultChanged(WebTagSearchResultRow? value)
    {
        GoToMatchCommand.NotifyCanExecuteChanged();
        RefreshCommands();
    }

    partial void OnSelectedRemoteRowChanged(RemoteTrackRow? value) => UpdateTrackFields();

    partial void OnIsBusyChanged(bool value) => RefreshCommands();

    partial void OnHasSearchCompletedChanged(bool value) => OnPropertyChanged(nameof(IsResultsEmpty));

    partial void OnConvertCoverEnabledChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        SearchCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        GoToMatchCommand.NotifyCanExecuteChanged();
        RetryThumbnailsCommand.NotifyCanExecuteChanged();
        ToggleManualLinkCommand.NotifyCanExecuteChanged();
        UseManualLinkCommand.NotifyCanExecuteChanged();
        WriteCommand.NotifyCanExecuteChanged();
        ReAutoMatchCommand.NotifyCanExecuteChanged();
        ClearMatchesCommand.NotifyCanExecuteChanged();
        UnbindRemoteCommand.NotifyCanExecuteChanged();
        RefreshCoverState();
    }

    private void ShowMessage(string message, string title, bool isError)
    {
        if (_shutdown)
        {
            return;
        }

        MessageRequested?.Invoke(this, new WebTagMessageRequestedEventArgs(message, title, isError));
    }

    private bool Confirm(string message, string title)
    {
        if (_shutdown)
        {
            return false;
        }

        var args = new WebTagConfirmRequestedEventArgs(message, title);
        ConfirmRequested?.Invoke(this, args);
        return args.Confirmed;
    }

    #endregion
}
