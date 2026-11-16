using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.App.Services;

namespace EchoGarage.App.ViewModels;

public sealed partial class TrackRowViewModel : ObservableObject
{
    [ObservableProperty]
    private int _rowNumber;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayable))]
    private bool _isMissing;

    [ObservableProperty]
    private BitmapSource? _coverImage;

    /// <summary>Larger cover used only by the album group header (cleared when it unloads).</summary>
    [ObservableProperty]
    private BitmapSource? _groupCoverImage;

    [ObservableProperty]
    private bool _isSelected;

    private GroupedCellLayout _cellLayout = GroupedCellLayout.Empty;
    private IReadOnlyList<TrackCellViewModel>? _cells;
    private IReadOnlyList<TrackCellViewModel>? _rightCells;
    private bool _cellsObserved;
    private bool _rightCellsObserved;

    /// <summary>
    /// Cells rendered by the grouped list. They are materialized on first binding access and cached
    /// afterwards, so only the rows the virtualized list actually displays pay for their cells.
    /// </summary>
    public IReadOnlyList<TrackCellViewModel> Cells
    {
        get
        {
            _cellsObserved = true;
            return _cells ??= _cellLayout.BuildCells(this, right: false);
        }
    }

    /// <summary>
    /// Cells whose columns are pinned to the right edge of the list. They are rendered in a
    /// separate right-aligned block so a growing left column cannot push them out of view.
    /// </summary>
    public IReadOnlyList<TrackCellViewModel> RightCells
    {
        get
        {
            _rightCellsObserved = true;
            return _rightCells ??= _cellLayout.BuildCells(this, right: true);
        }
    }

    /// <summary>
    /// Applies a new grouped-cell layout (visible columns + resolved templates). Rows that are
    /// already bound rebuild their cells immediately; unbound rows only keep the layout reference
    /// and build on first access, so a layout change never allocates cells for invisible rows.
    /// </summary>
    internal void SetCellLayout(GroupedCellLayout layout)
    {
        if (ReferenceEquals(_cellLayout, layout))
        {
            return;
        }

        _cellLayout = layout;
        if (_cellsObserved)
        {
            _cells = null;
            OnPropertyChanged(nameof(Cells));
        }

        if (_rightCellsObserved)
        {
            _rightCells = null;
            OnPropertyChanged(nameof(RightCells));
        }
    }

    /// <summary>Diagnostics: true once the list actually asked this row for cells.</summary>
    internal bool HasResolvedCells => _cells is not null || _rightCells is not null;

    private static readonly IReadOnlyDictionary<string, string> EmptySecondaryTexts =
        new Dictionary<string, string>();

    internal static readonly IReadOnlyDictionary<string, string?> EmptySecondaryFormats =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string?> _secondaryFormats = EmptySecondaryFormats;

    private IReadOnlyDictionary<string, string>? _secondaryTexts;
    private bool _secondaryTextsObserved;

    /// <summary>
    /// Optional second-row text keyed by column key. Values are produced from
    /// <see cref="PlaylistColumn.SecondaryFormat"/> templates; missing keys render nothing.
    /// Built on first binding access and cached, so only displayed rows pay for formatting.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecondaryTexts
    {
        get
        {
            _secondaryTextsObserved = true;
            return _secondaryTexts ??= BuildSecondaryTexts();
        }
    }

    /// <summary>
    /// Replaces the per-column secondary formats and invalidates the rendered texts. Keys with a
    /// blank format are omitted from <paramref name="formats"/>. Formatting itself is deferred
    /// until a row is displayed; already bound rows refresh via a property changed notification.
    /// </summary>
    public void ApplySecondaryFormats(IReadOnlyDictionary<string, string?> formats)
    {
        if (ReferenceEquals(_secondaryFormats, formats))
        {
            return;
        }

        _secondaryFormats = formats;
        _secondaryTexts = null;
        if (_secondaryTextsObserved)
        {
            OnPropertyChanged(nameof(SecondaryTexts));
        }
    }

    /// <summary>Diagnostics: true once the list actually asked this row for secondary texts.</summary>
    internal bool HasResolvedSecondaryTexts => _secondaryTexts is not null;

    private IReadOnlyDictionary<string, string> BuildSecondaryTexts()
    {
        if (_secondaryFormats.Count == 0)
        {
            return EmptySecondaryTexts;
        }

        var values = BuildTokenValues();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, format) in _secondaryFormats)
        {
            var text = SecondaryTextFormat.Format(format, values);
            if (text.Length > 0)
            {
                texts[key] = text;
            }
        }

        return texts;
    }

    private Dictionary<string, string?> BuildTokenValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["title"] = Track.DisplayTitle,
        ["artist"] = Track.Artist,
        ["album"] = Track.Album,
        ["albumArtist"] = Track.AlbumArtist,
        ["composer"] = Track.Composer,
        ["genre"] = Track.Genre,
        ["year"] = Track.Year is > 0 ? Track.Year.Value.ToString() : string.Empty,
        ["trackNumber"] = Track.TrackNumber > 0 ? Track.TrackNumber.ToString() : string.Empty,
        ["discNumber"] = Track.DiscNumber > 0 ? Track.DiscNumber.ToString() : string.Empty,
        ["duration"] = DisplayFormatter.Duration(Track.DurationMs),
        ["bitrate"] = DisplayFormatter.Bitrate(Track.Bitrate),
        ["sampleRate"] = DisplayFormatter.SampleRate(Track.SampleRate),
        ["channels"] = Track.Channels > 0 ? Track.Channels.ToString() : string.Empty,
        ["codec"] = Track.FormatDisplay,
        ["fileSize"] = DisplayFormatter.FileSize(Track.FileSize),
        ["addedAt"] = DisplayFormatter.Date(Track.AddedAtUtc),
        ["path"] = Track.Path,
        ["fileName"] = Track.FileName,
        ["comment"] = Track.Comment
    };

    public TrackRowViewModel(Track track, int rowNumber)
    {
        Track = track;
        _rowNumber = rowNumber;
        _isMissing = track.IsMissing;
    }

    public Track Track { get; private set; }

    public bool IsPlayable => !IsMissing;

    public string StatusGlyph => IsPlaying ? "▶" : string.Empty;

    public string IndexDisplay => TrackIndexFormatter.Format(Track.DiscNumber, Track.TrackNumber);

    public string Title => Track.DisplayTitle;
    public string Artist => Track.Artist;
    public string Album => Track.Album;
    public string AlbumArtist => Track.AlbumArtist;
    public string Genre => Track.Genre;
    public string Comment => Track.Comment;
    public string Year => Track.Year is > 0 ? Track.Year.Value.ToString() : string.Empty;
    public string TrackNumberText => Track.TrackNumber > 0 ? Track.TrackNumber.ToString() : string.Empty;
    public string DiscNumberText => Track.DiscNumber > 0 ? Track.DiscNumber.ToString() : string.Empty;
    public string DurationText => DisplayFormatter.Duration(Track.DurationMs);
    public string BitrateText => DisplayFormatter.Bitrate(Track.Bitrate);
    public string SampleRateText => DisplayFormatter.SampleRate(Track.SampleRate);
    public string Codec => Track.FormatDisplay;
    public string AddedText => DisplayFormatter.Date(Track.AddedAtUtc);
    public string FileSizeText => DisplayFormatter.FileSize(Track.FileSize);
    public string PathText => Track.Path;
    public string FileName => Track.FileName;

    public string RowToolTip => IsMissing
        ? $"{Track.Path}\n文件不存在或已被移动"
        : Track.Path;

    public int TrackNumberSort => Track.TrackNumber;
    public int DiscNumberSort => Track.DiscNumber;
    public int YearSort => Track.Year ?? 0;
    public long DurationSort => Track.DurationMs;
    public int BitrateSort => Track.Bitrate;
    public int SampleRateSort => Track.SampleRate;
    public DateTime AddedSort => Track.AddedAtUtc;
    public long FileSizeSort => Track.FileSize;

    public string AlbumGroupKey => string.IsNullOrWhiteSpace(Track.Album)
        ? "未知专辑"
        : $"{Track.AlbumArtist}\u001f{Track.Album}";

    public string AlbumGroupTitle => string.IsNullOrWhiteSpace(Track.Album) ? "未知专辑" : Track.Album;
    public string AlbumGroupArtist => string.IsNullOrWhiteSpace(Track.AlbumArtist) ? Track.Artist : Track.AlbumArtist;
    public string AlbumGroupYear => Year;

    public int SortIndex => Track.TrackNumber > 0 ? Track.TrackNumber : RowNumber;

    public void UpdateTrack(Track track)
    {
        Track = track;
        IsMissing = track.IsMissing;
        // Drop the cached texts; the empty-name notification re-reads them lazily from the new track.
        _secondaryTexts = null;
        OnPropertyChanged(string.Empty);
    }

    public void SetRowNumber(int number)
    {
        if (RowNumber == number)
        {
            return;
        }

        RowNumber = number;
    }
}

/// <summary>
/// Immutable snapshot of the grouped list's row-cell layout: the visible column headers (left flow
/// and right-pinned block) plus their resolved cell templates. One instance is shared by every row
/// of a rebuild; a row only creates its own <see cref="TrackCellViewModel"/> list when the
/// virtualized list displays it, so rows scrolled out of view never allocate cells.
/// </summary>
internal sealed class GroupedCellLayout
{
    public static GroupedCellLayout Empty { get; } = new([], [], [], []);

    private readonly GroupedColumnHeaderViewModel[] _leftHeaders;
    private readonly DataTemplate?[] _leftTemplates;
    private readonly GroupedColumnHeaderViewModel[] _rightHeaders;
    private readonly DataTemplate?[] _rightTemplates;

    public GroupedCellLayout(
        IReadOnlyList<GroupedColumnHeaderViewModel> leftHeaders,
        IReadOnlyList<DataTemplate?> leftTemplates,
        IReadOnlyList<GroupedColumnHeaderViewModel> rightHeaders,
        IReadOnlyList<DataTemplate?> rightTemplates)
    {
        _leftHeaders = [.. leftHeaders];
        _leftTemplates = [.. leftTemplates];
        _rightHeaders = [.. rightHeaders];
        _rightTemplates = [.. rightTemplates];
    }

    public int LeftColumnCount => _leftHeaders.Length;

    public int RightColumnCount => _rightHeaders.Length;

    internal IReadOnlyList<TrackCellViewModel> BuildCells(TrackRowViewModel row, bool right)
    {
        var headers = right ? _rightHeaders : _leftHeaders;
        if (headers.Length == 0)
        {
            return Array.Empty<TrackCellViewModel>();
        }

        var templates = right ? _rightTemplates : _leftTemplates;
        var cells = new TrackCellViewModel[headers.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var header = headers[i];
            cells[i] = new TrackCellViewModel
            {
                Key = header.Key,
                HeaderSource = header,
                Row = row,
                Template = templates[i]
            };
        }

        return cells;
    }
}
