using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Files;
using EchoGarage.Infrastructure.Logging;

namespace EchoGarage.Tests;

public sealed class FileOperationTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly FileOperationService _files;
    private readonly FakeRecycleBin _recycleBin = new();

    public FileOperationTests()
    {
        _paths = new AppPaths(_workspace.Combine("state"));
        _paths.EnsureCreated();
        _logger = new FileLogger(_paths);
        _files = new FileOperationService(_logger, _recycleBin);
    }

    // ------------------------------------------------------------------
    // FileDestinations
    // ------------------------------------------------------------------

    [Fact]
    public void Destinations_Normalize_TrimsDeduplicatesAndKeepsOrder()
    {
        var first = _workspace.CreateFolder("music-a");
        var second = _workspace.CreateFolder("music-b");

        var normalized = FileDestinations.Normalize(
            [second, $"  \"{first}\"  ", second.ToUpperInvariant(), first, null, "   ", "relative\\path"]);

        Assert.Equal([second, first], normalized);
    }

    [Fact]
    public void Destinations_Add_RespectsLimitAndDuplicates()
    {
        var values = new List<string>();
        for (var i = 0; i < FileDestinations.MaxCount; i++)
        {
            Assert.True(FileDestinations.Add(values, _workspace.CreateFolder($"dir-{i}")));
        }

        var extra = _workspace.CreateFolder("dir-extra");
        Assert.False(FileDestinations.Add(values, extra));
        Assert.False(FileDestinations.Add(values, values[0]));
        Assert.Equal(FileDestinations.MaxCount, values.Count);

        Assert.True(FileDestinations.Remove(values, values[0].ToUpperInvariant()));
        Assert.Equal(FileDestinations.MaxCount - 1, values.Count);
        Assert.True(FileDestinations.Add(values, extra));
    }

    // ------------------------------------------------------------------
    // FileOperationPlanner
    // ------------------------------------------------------------------

    [Fact]
    public void Planner_ResolvesConflicts_WithIncrementingSuffix()
    {
        var destination = _workspace.CreateFolder("dest");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(destination, "song.flac"),
            Path.Combine(destination, "song (2).flac"),
        };

        var resolved = FileOperationPlanner.ResolveTargetPath(
            destination, _workspace.Combine("music", "song.flac"), taken.Contains);

        Assert.Equal(Path.Combine(destination, "song (3).flac"), resolved);
    }

    [Fact]
    public void Planner_SameDirectory_ComparesIgnoreCase()
    {
        var directory = _workspace.CreateFolder("music");
        var file = Path.Combine(directory, "song.flac");

        Assert.True(FileOperationPlanner.IsSameDirectory(directory, file));
        Assert.True(FileOperationPlanner.IsSameDirectory(directory.ToUpperInvariant(), file));
        Assert.False(FileOperationPlanner.IsSameDirectory(_workspace.CreateFolder("other"), file));
    }

    // ------------------------------------------------------------------
    // FileOperationService
    // ------------------------------------------------------------------

    [Fact]
    public async Task Copy_CreatesTargetDirectory_AndDoesNotOverwrite()
    {
        var sourceDirectory = _workspace.CreateFolder("music");
        var source = Path.Combine(sourceDirectory, "song.wav");
        File.WriteAllText(source, "new");
        var destination = _workspace.Combine("dest");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "song.wav"), "existing");

        var result = await _files.CopyAsync([source], destination);

        var item = Assert.Single(result.Items);
        Assert.True(item.Succeeded);
        Assert.Equal(Path.Combine(destination, "song (2).wav"), item.TargetPath);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(destination, "song.wav")));
        Assert.True(File.Exists(source));
        Assert.Equal(1, result.Succeeded);
    }

    [Fact]
    public async Task Copy_CreatesMissingDestinationDirectory()
    {
        var source = _workspace.Combine("music", "a.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "a");
        var destination = _workspace.Combine("brand", "new", "folder");

        var result = await _files.CopyAsync([source], destination);

        Assert.True(result.Items[0].Succeeded);
        Assert.True(File.Exists(Path.Combine(destination, "a.wav")));
    }

    [Fact]
    public async Task Move_RemovesSourceFile()
    {
        var source = _workspace.Combine("music", "move-me.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "data");
        var destination = _workspace.CreateFolder("dest");

        var result = await _files.MoveAsync([source], destination);

        Assert.True(result.Items[0].Succeeded);
        Assert.False(File.Exists(source));
        Assert.Equal("data", File.ReadAllText(Path.Combine(destination, "move-me.wav")));
    }

    [Fact]
    public async Task Copy_SameDirectory_IsRejected()
    {
        var directory = _workspace.CreateFolder("music");
        var source = Path.Combine(directory, "song.wav");
        File.WriteAllText(source, "data");

        var result = await _files.CopyAsync([source], directory);

        var item = Assert.Single(result.Items);
        Assert.False(item.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(item.Error));
    }

    [Fact]
    public async Task Copy_MissingSource_ReportsFailure()
    {
        var result = await _files.CopyAsync([_workspace.Combine("nope.wav")], _workspace.CreateFolder("dest"));

        Assert.False(result.Items[0].Succeeded);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task Delete_UsesRecycleBin_AndReportsFailures()
    {
        var a = _workspace.Combine("music", "a.wav");
        var b = _workspace.Combine("music", "b.wav");
        var missing = _workspace.Combine("music", "missing.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");
        _recycleBin.Rejected.Add(b);

        var result = await _files.DeleteToRecycleBinAsync([a, b, missing]);

        Assert.True(result.Items.Single(i => i.SourcePath == a).Succeeded);
        Assert.False(result.Items.Single(i => i.SourcePath == b).Succeeded);
        Assert.False(result.Items.Single(i => i.SourcePath == missing).Succeeded);
        Assert.Contains(a, _recycleBin.Deleted);
        Assert.Equal(1, result.Succeeded);
        Assert.Equal(2, result.Failed);
    }

    public void Dispose() => _workspace.Dispose();

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public List<string> Deleted { get; } = [];
        public HashSet<string> Rejected { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryDelete(string path)
        {
            if (Rejected.Contains(path))
            {
                return false;
            }

            Deleted.Add(path);
            File.Delete(path);
            return !File.Exists(path);
        }
    }
}
