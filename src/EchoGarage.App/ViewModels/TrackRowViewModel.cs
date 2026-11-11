using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media.Imaging;
using EchoGarage.Core.Models;
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

    /// <summary>True for the first visible row of an album group (it carries the album cover).</summary>
    [ObservableProperty]
    private bool _isAlbumGroupStart;

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
    public string RatingText => DisplayFormatter.Rating(Track.Rating);
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
    public int RatingSort => Track.Rating;
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
