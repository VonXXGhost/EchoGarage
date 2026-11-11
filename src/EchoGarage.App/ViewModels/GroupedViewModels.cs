using System.Windows;

namespace EchoGarage.App.ViewModels;

/// <summary>One cell of a song row in the grouped list (column key + width).</summary>
public sealed class TrackCellViewModel
{
    public required string Key { get; init; }
    public required double Width { get; init; }
    public required TrackRowViewModel Row { get; init; }
    /// <summary>Cell template resolved from the window's "Cell.&lt;Column&gt;" resources.</summary>
    public DataTemplate? Template { get; init; }
}

/// <summary>Header cell above the grouped song list, aligned with the row cells.</summary>
public sealed class GroupedColumnHeaderViewModel
{
    public required string Key { get; init; }
    public required string Header { get; init; }
    public required double Width { get; init; }
    public string SortGlyph { get; init; } = string.Empty;
}

/// <summary>Maps playlist column keys to the row property used for sorting.</summary>
public static class TrackSortMap
{
    public static readonly IReadOnlyDictionary<string, string?> ByKey =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["cover"] = null,
            ["status"] = null,
            ["index"] = nameof(TrackRowViewModel.RowNumber),
            ["title"] = nameof(TrackRowViewModel.Title),
            ["artist"] = nameof(TrackRowViewModel.Artist),
            ["album"] = nameof(TrackRowViewModel.Album),
            ["albumArtist"] = nameof(TrackRowViewModel.AlbumArtist),
            ["trackNumber"] = nameof(TrackRowViewModel.TrackNumberSort),
            ["discNumber"] = nameof(TrackRowViewModel.DiscNumberSort),
            ["year"] = nameof(TrackRowViewModel.YearSort),
            ["genre"] = nameof(TrackRowViewModel.Genre),
            ["duration"] = nameof(TrackRowViewModel.DurationSort),
            ["bitrate"] = nameof(TrackRowViewModel.BitrateSort),
            ["sampleRate"] = nameof(TrackRowViewModel.SampleRateSort),
            ["codec"] = nameof(TrackRowViewModel.Codec),
            ["rating"] = nameof(TrackRowViewModel.RatingSort),
            ["addedAt"] = nameof(TrackRowViewModel.AddedSort),
            ["fileSize"] = nameof(TrackRowViewModel.FileSizeSort),
            ["path"] = nameof(TrackRowViewModel.PathText),
            ["comment"] = nameof(TrackRowViewModel.Comment)
        };
}
