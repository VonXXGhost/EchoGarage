using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using EchoGarage.App.Services;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.App.ViewModels;

/// <summary>Web 标签源下拉项；IWebTagSource 无法直接绑定 DisplayName（避免依赖源实现的 ToString）。</summary>
public sealed class WebTagSourceOption
{
    public WebTagSourceOption(IWebTagSource source)
    {
        Source = source;
    }

    public IWebTagSource Source { get; }

    public string Id => Source.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Source.DisplayName) ? Source.Id : Source.DisplayName;

    public override string ToString() => DisplayName;
}

/// <summary>专辑组下拉项：一次只处理一个专辑组（ADR-022）。</summary>
public sealed class AlbumGroupOption
{
    public AlbumGroupOption(IReadOnlyList<Track> tracks, string display)
    {
        Tracks = tracks;
        Display = display;
    }

    public IReadOnlyList<Track> Tracks { get; }

    public string Display { get; }

    public override string ToString() => Display;
}

/// <summary>远程曲目行（第三步左侧列表）。</summary>
public sealed partial class RemoteTrackRow : ObservableObject
{
    private bool _suppressOptionChange;

    public RemoteTrackRow(WebTagTrack track, int index)
    {
        Track = track;
        Index = index;
        OrdinalDisplay = track.OrdinalDisplay;
        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = string.IsNullOrWhiteSpace(track.DisplayArtist) ? "—" : track.DisplayArtist;
        ComposerDisplay = track.DisplayComposer;
        DurationDisplay = track.DisplayDuration;
    }

    public WebTagTrack Track { get; }

    public int Index { get; }

    public string OrdinalDisplay { get; }

    public string TitleDisplay { get; }

    public string ArtistDisplay { get; }

    public string ComposerDisplay { get; }

    public string DurationDisplay { get; }

    /// <summary>该行参与写入（歌曲级字段只写已勾选且已绑定远程曲目的行）。</summary>
    [ObservableProperty]
    private bool _isWriteEnabled = true;

    [ObservableProperty]
    private LocalFileRow? _boundLocal;

    [ObservableProperty]
    private WebTagMatchKind _matchKind = WebTagMatchKind.None;

    [ObservableProperty]
    private LocalFileOption? _selectedLocalOption;

    [ObservableProperty]
    private bool _isDragOver;

    public bool IsBound => BoundLocal is not null;

    public bool IsWriteToggleEnabled => BoundLocal is not null;

    public string BoundFileDisplay => BoundLocal?.TitleDisplay ?? "未匹配";

    public string MatchKindDisplay => MatchKind switch
    {
        WebTagMatchKind.TrackNumber => "音轨号匹配",
        WebTagMatchKind.Title => "标题匹配",
        WebTagMatchKind.Manual => "手动绑定",
        _ => "未匹配"
    };

    // 供全局 DataGridRow 样式的 DataTrigger 绑定，避免绑定错误。
    public bool IsMissing => false;

    public bool IsPlaying => false;

    internal Action<RemoteTrackRow, LocalFileRow?>? OptionChanged { get; set; }

    internal void SetSelectedOptionSilently(LocalFileOption? option)
    {
        _suppressOptionChange = true;
        try
        {
            SelectedLocalOption = option;
        }
        finally
        {
            _suppressOptionChange = false;
        }
    }

    partial void OnBoundLocalChanged(LocalFileRow? value)
    {
        OnPropertyChanged(nameof(IsBound));
        OnPropertyChanged(nameof(IsWriteToggleEnabled));
        OnPropertyChanged(nameof(BoundFileDisplay));
    }

    partial void OnMatchKindChanged(WebTagMatchKind value) => OnPropertyChanged(nameof(MatchKindDisplay));

    partial void OnSelectedLocalOptionChanged(LocalFileOption? value)
    {
        if (_suppressOptionChange)
        {
            return;
        }

        OptionChanged?.Invoke(this, value?.File);
    }
}

/// <summary>本地文件行（第三步右侧列表与左侧行内下拉的共同数据源）。</summary>
public sealed partial class LocalFileRow : ObservableObject
{
    public LocalFileRow(Track track, int index)
    {
        Track = track;
        Index = index;
        TitleDisplay = track.DisplayTitle;
        FileNameDisplay = string.IsNullOrWhiteSpace(track.FileName) ? Path.GetFileName(track.Path) : track.FileName;
        DurationDisplay = DisplayFormatter.Duration(track.DurationMs);
        TrackNumberDisplay = track.TrackNumber > 0
            ? track.DiscNumber > 0
                ? $"{track.DiscNumber}-{track.TrackNumber:00}"
                : track.TrackNumber.ToString("00")
            : "无序号";
    }

    public Track Track { get; }

    public int Index { get; }

    public string TitleDisplay { get; }

    public string FileNameDisplay { get; }

    public string DurationDisplay { get; }

    public string TrackNumberDisplay { get; }

    public bool IsWritable => !Track.IsMissing;

    public bool IsMissing => Track.IsMissing;

    public bool IsPlaying => false;

    [ObservableProperty]
    private RemoteTrackRow? _boundRemote;

    [ObservableProperty]
    private string _matchStatus = "未匹配";

    public bool IsBound => BoundRemote is not null;

    partial void OnBoundRemoteChanged(RemoteTrackRow? value) => OnPropertyChanged(nameof(IsBound));
}

/// <summary>左侧行内兜底下拉项；File 为 null 表示解除绑定。</summary>
public sealed record LocalFileOption(LocalFileRow? File, string Display)
{
    public override string ToString() => Display;
}

/// <summary>远程曲目字段预览行（歌曲级 / 专辑级共用）。</summary>
public sealed partial class WebTagFieldRow : ObservableObject
{
    public WebTagFieldRow(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _displayValue = WebTagSearchViewModel.NoDataText;

    [ObservableProperty]
    private bool _hasValue;

    [ObservableProperty]
    private bool _isInteractive = true;

    /// <summary>无值时 checkbox 置灰（但保留勾选记忆）。</summary>
    public bool IsCheckboxEnabled => HasValue || IsInteractive;

    partial void OnHasValueChanged(bool value) => OnPropertyChanged(nameof(IsCheckboxEnabled));

    partial void OnIsInteractiveChanged(bool value) => OnPropertyChanged(nameof(IsCheckboxEnabled));
}

/// <summary>第二步搜索结果行。</summary>
public sealed partial class WebTagSearchResultRow : ObservableObject
{
    public WebTagSearchResultRow(WebTagSearchItem item)
    {
        Item = item;
    }

    public WebTagSearchItem Item { get; }

    public string DisplayTitle => Item.DisplayTitle;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Item.DisplayArtist) ? "—" : Item.DisplayArtist;

    public string TrackCountDisplay => Item.TrackCountDisplay;

    public string YearDisplay => Item.YearDisplay;

    public bool IsDerived => Item.IsDerived;

    public string KindNote => IsDerived ? "由单曲结果推导" : "专辑";

    [ObservableProperty]
    private ImageSource? _thumbnail;

    public bool IsMissing => false;

    public bool IsPlaying => false;
}

/// <summary>请求用户确认（窗口负责 MessageBox）。</summary>
public sealed class WebTagMessageRequestedEventArgs(string message, string title, bool isError) : EventArgs
{
    public string Message { get; } = message;

    public string Title { get; } = title;

    public bool IsError { get; } = isError;
}

/// <summary>请求用户确认（窗口负责 MessageBox，回填 <see cref="Confirmed"/>）。</summary>
public sealed class WebTagConfirmRequestedEventArgs(string message, string title) : EventArgs
{
    public string Message { get; } = message;

    public string Title { get; } = title;

    public bool Confirmed { get; set; }
}

/// <summary>写入计划已生成，请求关闭窗口并返回结果。</summary>
public sealed class WebTagApplyRequestedEventArgs(WebTagApplyPlan plan) : EventArgs
{
    public WebTagApplyPlan Plan { get; } = plan;
}
