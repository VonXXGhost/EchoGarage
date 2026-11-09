using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Data;
using EchoGarage.Infrastructure.Library;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;

namespace EchoGarage.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly SqliteLibraryRepository _library;
    private readonly LibraryScanner _scanner;

    public ScannerTests()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var database = new SqliteDatabase(paths, logger);
        _library = new SqliteLibraryRepository(database, logger);
        _library.InitializeAsync().GetAwaiter().GetResult();
        _scanner = new LibraryScanner(_library, new TagLibTagService(logger), logger);
    }

    [Fact]
    public async Task Scan_IndexesAudioFiles_AndSkipsNonAudio()
    {
        var folderPath = _workspace.CreateFolder("Album");
        TestAudio.CreateWav(Path.Combine(folderPath, "01.wav"), seconds: 1);
        TestAudio.CreateWav(Path.Combine(folderPath, "02.wav"), seconds: 2);
        File.WriteAllText(Path.Combine(folderPath, "cover.txt"), "not audio");
        Directory.CreateDirectory(Path.Combine(folderPath, "Sub"));
        TestAudio.CreateWav(Path.Combine(folderPath, "Sub", "03.wav"), seconds: 1);

        var folder = await _library.AddFolderAsync(folderPath);
        var result = await _scanner.ScanFolderAsync(folder);

        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Removed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, await _library.GetLibraryTrackCountAsync());

        var tracks = await _library.GetLibraryTracksAsync();
        Assert.All(tracks, t => Assert.Equal("WAV", t.Codec));
        Assert.All(tracks, t => Assert.True(t.DurationMs > 500));
        Assert.All(tracks, t => Assert.NotNull(t.CoverKey));
    }

    [Fact]
    public async Task Rescan_SkipsUnchangedFiles_AndReindexesModifiedOnes()
    {
        var folderPath = _workspace.CreateFolder("Album");
        var file = TestAudio.CreateWav(Path.Combine(folderPath, "01.wav"), seconds: 1);
        var folder = await _library.AddFolderAsync(folderPath);

        var first = await _scanner.ScanFolderAsync(folder);
        Assert.Equal(1, first.Added);

        var second = await _scanner.ScanFolderAsync(folder);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Skipped);

        TestAudio.CreateWav(file, seconds: 3);
        TestAudio.Touch(file);

        var third = await _scanner.ScanFolderAsync(folder);
        Assert.Equal(1, third.Added);
        Assert.Equal(0, third.Skipped);

        var track = await _library.GetTrackByPathAsync(file);
        Assert.NotNull(track);
        Assert.InRange(track!.DurationMs, 2500, 3500);
    }

    [Fact]
    public async Task Rescan_MarksDeletedFilesMissing()
    {
        var folderPath = _workspace.CreateFolder("Album");
        var keep = TestAudio.CreateWav(Path.Combine(folderPath, "keep.wav"));
        var remove = TestAudio.CreateWav(Path.Combine(folderPath, "remove.wav"));
        var folder = await _library.AddFolderAsync(folderPath);
        await _scanner.ScanFolderAsync(folder);

        File.Delete(remove);
        var result = await _scanner.ScanFolderAsync(folder);

        Assert.Equal(1, result.Removed);
        var remaining = await _library.GetLibraryTracksAsync();
        Assert.Single(remaining);
        Assert.Equal(keep, remaining[0].Path);
    }

    [Fact]
    public async Task IndexFile_ExternalTrack_IsNotPartOfLibrary()
    {
        var path = TestAudio.CreateWav(_workspace.Combine("outside", "external.wav"));

        var track = await _scanner.IndexFileAsync(path, inLibrary: false);

        Assert.NotNull(track);
        Assert.False(track!.IsInLibrary);
        Assert.Null(track.FolderId);
        Assert.Equal(0, await _library.GetLibraryTrackCountAsync());
    }

    [Fact]
    public async Task Progress_IsReported()
    {
        var folderPath = _workspace.CreateFolder("Album");
        for (var i = 0; i < 5; i++)
        {
            TestAudio.CreateWav(Path.Combine(folderPath, $"{i}.wav"));
        }

        var folder = await _library.AddFolderAsync(folderPath);
        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(reports.Add);

        await _scanner.ScanFolderAsync(folder, progress);

        Assert.NotEmpty(reports);
        Assert.Contains(reports, r => r.Percent is > 0 and <= 100);
    }

    public void Dispose() => _workspace.Dispose();
}
