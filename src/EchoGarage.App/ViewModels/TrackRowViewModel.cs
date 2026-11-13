using CommunityToolkit.Mvvm.ComponentModel;
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

    private IReadOnlyList<TrackCellViewModel> _cells = [];

    /// <summary>Cells rendered by the grouped list; rebuilt when the column layout changes.</summary>
    public IReadOnlyList<TrackCellViewModel> Cells
    {
        get => _cells;
        set => SetProperty(ref _cells, value);
    }

    private IReadOnlyList<TrackCellViewModel> _rightCells = [];

    /// <summary>
    /// Cells whose columns are pinned to the right edge of the list. They are rendered in a
    /// separate right-aligned block so a growing left column cannot push them out of view.
    /// </summary>
    public IReadOnlyList<TrackCellViewModel> RightCells
    {
        get => _rightCells;
        set => SetProperty(ref _rightCells, value);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptySecondaryTexts =
        new Dictionary<string, string>();

    private IReadOnlyDictionary<string, string?> _secondaryFormats =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, string> _secondaryTexts = EmptySecondaryTexts;

    /// <summary>
    /// Optional second-row text keyed by column key. Values are produced from
    /// <see cref="PlaylistColumn.SecondaryFormat"/> templates; missing keys render nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecondaryTexts
    {
        get => _secondaryTexts;
        set => SetProperty(ref _secondaryTexts, value);
    }

    /// <summary>
    /// Replaces the per-column secondary formats and re-renders the secondary texts from the
    /// current track. Keys with a blank format are omitted from <paramref name="formats"/>.
    /// </summary>
    public void ApplySecondaryFormats(IReadOnlyDictionary<string, string?> formats)
    {
        _secondaryFormats = formats;
        RebuildSecondaryTexts();
    }

    private void RebuildSecondaryTexts()
    {
        if (_secondaryFormats.Count == 0)
        {
            if (_secondaryTexts.Count > 0)
            {
                SecondaryTexts = EmptySecondaryTexts;
            }

            return;
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

        SecondaryTexts = texts;
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

    public string IndexDisplay => Track.TrackNumber > 0
        ? Track.TrackNumber.ToString("00")
        : RowNumber.ToString();

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
        RebuildSecondaryTexts();
        OnPropertyChanged(string.Empty);
    }

    public void SetRowNumber(int number)
    {
        if (RowNumber == number)
        {
            return;
        }

        RowNumber = number;
        OnPropertyChanged(nameof(IndexDisplay));
    }
}
