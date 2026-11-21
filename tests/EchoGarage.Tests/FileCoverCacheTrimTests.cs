using EchoGarage.Infrastructure.Caching;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;

namespace EchoGarage.Tests;

public sealed class FileCoverCacheTrimTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task TrimAsync_DeletesOldestCoversBeyondLimit_AndCompanionStamps()
    {
        var covers = _workspace.Combine("data", "Cache", "Covers");
        var cache = CreateCache(covers, limitMb: 1);
        // 20 × 100 KB = 2 MB，超过 1 MB 上限。
        var oldest = WriteCover(covers, "album_00", minutesAgo: 60);
        for (var index = 1; index < 19; index++)
        {
            WriteCover(covers, $"album_{index:00}", minutesAgo: 60 - index);
        }

        var newest = WriteCover(covers, "album_19", minutesAgo: 1);

        var removed = await cache.TrimAsync();

        Assert.True(removed > 0);
        Assert.False(File.Exists(oldest));
         Assert.False(File.Exists(Path.Combine(covers, "album_00.stamp")));
        Assert.True(File.Exists(newest));
        var remaining = Directory.GetFiles(covers, "*.jpg").Sum(path => new FileInfo(path).Length);
        Assert.True(remaining <= 1L * 1024 * 1024 * 9 / 10, $"裁剪后仍有 {remaining} 字节");
    }

    [Fact]
    public async Task TrimAsync_DoesNothingWhenUnderLimitOrUnlimited()
    {
        var covers = _workspace.Combine("data", "Cache", "Covers");
        var cache = CreateCache(covers, limitMb: 0);
        WriteCover(covers, "album_00", minutesAgo: 60);
        WriteCover(covers, "album_01", minutesAgo: 30);

        Assert.Equal(0, await cache.TrimAsync());
        Assert.Equal(2, Directory.GetFiles(covers, "*.jpg").Length);

        cache.LimitMb = 16;
        Assert.Equal(0, await cache.TrimAsync());
        Assert.Equal(2, Directory.GetFiles(covers, "*.jpg").Length);
    }

    private FileCoverCache CreateCache(string coversDirectory, int limitMb)
    {
        var paths = new AppPaths(_workspace.Root);
        paths.EnsureCreated();
        Directory.CreateDirectory(coversDirectory);
        return new FileCoverCache(coversDirectory, new FileLogger(paths)) { LimitMb = limitMb };
    }

    private static string WriteCover(string directory, string key, int minutesAgo)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".jpg");
        File.WriteAllBytes(path, new byte[100 * 1024]);
        // 伴生标记以封面键（无扩展名）命名，与 FileCoverCache 的 stampPath 一致。
        var stampPath = Path.Combine(directory, key + ".stamp");
        File.WriteAllText(stampPath, "stamp");
        var stamp = DateTime.UtcNow.AddMinutes(-minutesAgo);
        File.SetLastWriteTimeUtc(path, stamp);
        File.SetLastWriteTimeUtc(stampPath, stamp);
        return path;
    }
}
