using System.Security.Cryptography;
using System.Text;

namespace EchoGarage.Core.Services;

/// <summary>
/// Identity of a database row used to decide whether a file needs re-indexing.
/// Plain mutable class so Dapper can materialize it directly.
/// </summary>
public sealed class TrackState
{
    public string Path { get; set; } = string.Empty;
    public DateTime ModifiedAtUtc { get; set; }
    public long FileSize { get; set; }
    public bool IsMissing { get; set; }
}

/// <summary>
/// Cover art lookup request. Covers are cached per album when album metadata exists,
/// otherwise per file, so an album with 15 tracks opens the file only once per cache miss.
/// </summary>
public sealed record CoverRequest(string TrackPath, string CoverKey, DateTime ModifiedAtUtc, long FileSize);

public static class CoverKeyFactory
{
    public static string ForTrack(string path, string album, string albumArtist)
    {
        if (!string.IsNullOrWhiteSpace(album))
        {
            var identity = "album|" + albumArtist.Trim().ToUpperInvariant() + "|" + album.Trim().ToUpperInvariant();
            return "album_" + Hash(identity);
        }

        return "file_" + Hash(path.Trim().ToUpperInvariant());
    }

    public static string ForFolder(string folderPath) => "folder_" + Hash(folderPath.Trim().ToUpperInvariant());

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..20].ToLowerInvariant();
    }
}
