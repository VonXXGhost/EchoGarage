using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// Decides whether a cached track row is older than the file on disk.
/// The library scanner uses the same criteria when it walks library folders;
/// this helper lets the app refresh rows that are outside every library folder
/// (for example files added directly to a playlist), which the scanner never visits.
/// </summary>
public static class TrackRefresh
{
    public static bool NeedsRefresh(Track track, long fileSize, DateTime modifiedAtUtc)
        => track.DurationMs <= 0
           || track.FileSize != fileSize
           || track.ModifiedAtUtc != modifiedAtUtc;
}
