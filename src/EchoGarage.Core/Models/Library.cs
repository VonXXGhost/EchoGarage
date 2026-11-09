namespace EchoGarage.Core.Models;

public sealed class LibraryFolder
{
    public long Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime AddedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastScanAtUtc { get; set; }
    public int TrackCount { get; set; }

    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }
}

public enum LibraryChangeKind
{
    Added,
    Updated,
    Removed
}

public sealed class LibraryTrackChangedEventArgs : EventArgs
{
    public required LibraryChangeKind Kind { get; init; }
    public required string Path { get; init; }
    public Track? Track { get; init; }
}

public sealed record ScanProgress(
    string FolderPath,
    int Processed,
    int Total,
    string? CurrentFile,
    string Phase)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Processed * 100.0 / Total, 0, 100);
}

public sealed record ScanResult(
    string FolderPath,
    int Added,
    int Updated,
    int Removed,
    int Skipped,
    int Failed,
    TimeSpan Elapsed);
