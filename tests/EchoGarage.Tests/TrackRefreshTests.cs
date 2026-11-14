using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class TrackRefreshTests
{
    private static Track CreateTrack(long fileSize = 1024, DateTime modifiedAtUtc = default, long durationMs = 1000)
        => new()
        {
            Path = @"C:\Music\sample.flac",
            Title = "Sample",
            FileSize = fileSize,
            ModifiedAtUtc = modifiedAtUtc,
            DurationMs = durationMs
        };

    [Fact]
    public void NeedsRefresh_ReturnsFalse_WhenSizeAndTimestampMatch()
    {
        var modified = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        var track = CreateTrack(1024, modified);

        Assert.False(TrackRefresh.NeedsRefresh(track, 1024, modified));
    }

    [Fact]
    public void NeedsRefresh_ReturnsTrue_WhenSizeDiffers()
    {
        var modified = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        var track = CreateTrack(1024, modified);

        Assert.True(TrackRefresh.NeedsRefresh(track, 2048, modified));
    }

    [Fact]
    public void NeedsRefresh_ReturnsTrue_WhenTimestampDiffers()
    {
        var track = CreateTrack(1024, new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc));

        Assert.True(TrackRefresh.NeedsRefresh(
            track,
            1024,
            new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void NeedsRefresh_ReturnsTrue_WhenCachedDurationIsMissing()
    {
        var modified = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        var track = CreateTrack(1024, modified, durationMs: 0);

        Assert.True(TrackRefresh.NeedsRefresh(track, 1024, modified));
    }
}
