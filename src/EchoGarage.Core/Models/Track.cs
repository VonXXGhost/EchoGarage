namespace EchoGarage.Core.Models;

/// <summary>
/// A media file known to EchoGarage. Rows can belong to the media library
/// (<see cref="IsInLibrary"/> = true) or exist only as a playlist extern.
/// Metadata is cached in SQLite; the file remains the source of truth.
/// </summary>
public sealed class Track
{
    public long Id { get; set; }
    public long PlaylistItemId { get; set; }
    public string Path { get; set; } = string.Empty;
    public long? FolderId { get; set; }
    public bool IsInLibrary { get; set; }
    public bool IsMissing { get; set; }

    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public int? Year { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public int Rating { get; set; }

    public long DurationMs { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public string Codec { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAtUtc { get; set; }
    public int PlayCount { get; set; }

    /// <summary>Name of the cover image inside the configured cache directory.</summary>
    public string? CoverKey { get; set; }

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
    public string FileName => System.IO.Path.GetFileName(Path);
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Title;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;
    public string FormatDisplay => string.IsNullOrWhiteSpace(Codec)
        ? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()
        : Codec;

    public Track Clone() => (Track)MemberwiseClone();
}
