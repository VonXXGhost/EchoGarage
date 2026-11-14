namespace EchoGarage.Core.Models;

public enum OutputMode
{
    Shared,
    Exclusive
}

public enum AudioPlaybackState
{
    Stopped,
    Loading,
    Playing,
    Paused
}

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsDefault)
{
    public string DisplayName => FriendlyName;
}

public sealed class AudioPlaybackStateChangedEventArgs : EventArgs
{
    public required AudioPlaybackState State { get; init; }
    public string? TrackPath { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// One additional tag field found in the file that is not part of the common
/// editable fields (e.g. BPM, ISRC, MusicBrainz ids, custom ID3 frames).
/// </summary>
public sealed record AudioTagEntry(string Name, string Value);

public sealed record AudioTagData(
    string Title,
    string Artist,
    string Album,
    string AlbumArtist,
    string Composer,
    string Genre,
    string Comment,
    int? Year,
    int TrackNumber,
    int DiscNumber,
    long DurationMs,
    int Bitrate,
    int SampleRate,
    int Channels,
    string Codec,
    byte[]? CoverBytes,
    string? CoverMime)
{
    /// <summary>Other tags present in the file, shown read-only in the tag editor.</summary>
    public IReadOnlyList<AudioTagEntry> AdditionalTags { get; init; } = [];
}

public sealed class TagEditModel
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string AlbumArtist { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public byte[]? NewCoverBytes { get; set; }
    public string? NewCoverMime { get; set; }
    public bool RemoveCover { get; set; }
}

public sealed class AudioFileDetails
{
    public required Track Track { get; init; }
    public byte[]? CoverBytes { get; init; }
    public string? CoverMime { get; init; }
}
