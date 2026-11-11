namespace EchoGarage.Core.Services;

/// <summary>
/// Rules that decide when the album-grouped list can be shown. Grouping is only
/// meaningful when the playlist is sorted by album or album artist: any other sort
/// order interleaves albums, so the list must stay a faithful flat sort instead.
/// </summary>
public static class PlaylistSortPolicy
{
    /// <summary>Row property used when sorting by the album column.</summary>
    public const string Album = "Album";

    /// <summary>Row property used when sorting by the album-artist column.</summary>
    public const string AlbumArtist = "AlbumArtist";

    public static bool SupportsAlbumGrouping(string? sortColumn) =>
        string.Equals(sortColumn, Album, StringComparison.Ordinal) ||
        string.Equals(sortColumn, AlbumArtist, StringComparison.Ordinal);

    /// <summary>Sort property that reproduces the playlist's default order.</summary>
    public const string DefaultOrder = "RowNumber";
}
