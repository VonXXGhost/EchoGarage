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
/// <see cref="Name"/> is the display label; <see cref="Kind"/> and <see cref="Key"/>
/// identify the underlying tag item for writing it back.
/// </summary>
public sealed record AudioTagEntry(string Name, string Value)
{
    public string Kind { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;
}

/// <summary>额外标签的写入目标类型。</summary>
public static class AdditionalTagKinds
{
    /// <summary>ID3v2 标准文本帧（如 TIT1 / TPUB / TBPM）。</summary>
    public const string Id3Text = "id3-text";

    /// <summary>ID3v2 TXXX 自定义帧，Key 为 Description。</summary>
    public const string Id3UserText = "id3-user-text";

    /// <summary>Xiph (Vorbis) 注释字段，Key 为字段名。</summary>
    public const string Xiph = "xiph";

    /// <summary>Apple (M4A) 顶层 atom，Key 为 atom 名（Latin-1）。</summary>
    public const string AppleAtom = "apple-atom";

    /// <summary>Apple (M4A) 自由格式 atom（----），Key 由 mean + name 组成。</summary>
    public const string AppleFreeform = "apple-freeform";

    /// <summary>ASF (WMA) 描述符，Key 为描述符名称。</summary>
    public const string Asf = "asf";
}

/// <summary>额外标签的稳定 key 组合规则。</summary>
public static class AdditionalTagKey
{
    public const char AppleFreeformSeparator = '\u001f';

    public static string AppleFreeform(string mean, string name) =>
        $"{mean}{AppleFreeformSeparator}{name}";

    public static (string Mean, string Name) SplitAppleFreeform(string key)
    {
        var separator = key.IndexOf(AppleFreeformSeparator);
        return separator < 0
            ? (key, string.Empty)
            : (key[..separator], key[(separator + 1)..]);
    }
}

/// <summary>一次额外标签写入；<see cref="Value"/> 为空表示删除该项。</summary>
public sealed record AudioTagChange(string Kind, string Key, string? Value)
{
    public bool IsRemoval => string.IsNullOrWhiteSpace(Value);
}

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
    /// <summary>Other tags present in the file, shown in the tag editor.</summary>
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

    /// <summary>额外标签改动；值为空表示删除。未列出的额外标签保持原值。</summary>
    public List<AudioTagChange> AdditionalTagChanges { get; } = [];
}

public sealed class AudioFileDetails
{
    public required Track Track { get; init; }
    public byte[]? CoverBytes { get; init; }
    public string? CoverMime { get; init; }
}
