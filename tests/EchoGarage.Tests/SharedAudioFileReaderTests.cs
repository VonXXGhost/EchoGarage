using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Audio;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;

namespace EchoGarage.Tests;

public sealed class SharedAudioFileReaderTests
{
    [Fact]
    public void WriteTags_WhileReaderIsOpen_ReplacesFileAndPlaybackContinues()
    {
        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var path = workspace.Combine("seamless.wav");
        TestAudio.CreateWav(path, seconds: 2);
        var tags = new TagLibTagService(logger);

        using var reader = new SharedAudioFileReader(path);
        var buffer = new float[44100];
        Assert.True(reader.Read(buffer, 0, buffer.Length) > 0);
        var positionBeforeWrite = reader.CurrentTime;

        // The reader keeps the file open with FileShare.ReadWrite | FileShare.Delete, so the tag
        // writer must detect the lock and swap in a new copy instead of writing in place.
        tags.Write(path, new TagEditModel { Title = "Seamless", Artist = "Tester" });

        Assert.Equal("Seamless", tags.Read(path, includeCover: false).Title);
        Assert.True(reader.Read(buffer, 0, 1000) > 0);
        Assert.True(reader.CurrentTime > positionBeforeWrite);
    }
}
