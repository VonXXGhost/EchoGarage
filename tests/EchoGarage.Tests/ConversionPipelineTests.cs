using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;

namespace EchoGarage.Tests;

/// <summary>
/// End-to-end conversion tests that exercise the bundled tools in <c>tools\</c>.
/// They are skipped when the tools are missing (e.g. a source-only checkout).
/// </summary>
public sealed class ConversionPipelineTests
{
    private static string ToolsDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools"));

    private static readonly object ToolCopyLock = new();
    private static readonly Dictionary<string, string?> ToolCopies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a runnable copy of a bundled tool. Some restricted environments (for example
    /// sandboxed agent sessions) let child processes create files only when the executable
    /// itself lives outside the workspace, so the tools are copied to the user temp folder
    /// before running. Returns <c>null</c> when the tool is not bundled (source-only checkout).
    /// </summary>
    private static string? ResolveTool(string name)
    {
        lock (ToolCopyLock)
        {
            if (ToolCopies.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var source = Path.Combine(ToolsDirectory, name);
            if (!File.Exists(source))
            {
                ToolCopies[name] = null;
                return null;
            }

            try
            {
                var directory = Path.Combine(Path.GetTempPath(), "EchoGarage.Tests", "tools");
                Directory.CreateDirectory(directory);
                var target = Path.Combine(directory, name);
                if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
                {
                    File.Copy(source, target, overwrite: true);
                }

                ToolCopies[name] = target;
                return target;
            }
            catch (IOException)
            {
                ToolCopies[name] = source;
                return source;
            }
            catch (UnauthorizedAccessException)
            {
                ToolCopies[name] = source;
                return source;
            }
        }
    }

    [Fact]
    public async Task Convert_WavToFlac_CopiesTagsAndProducesFlacFile()
    {
        var flacPath = ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var source = TestAudio.CreateWav(workspace.Combine("source.wav"));
        WriteTags(logger, source);

        var preset = new ConversionPreset
        {
            Name = "flac",
            Format = ConversionFormat.Flac,
            Quality = 5,
            BitDepth = ConversionBitDepth.Source,
            FileNameTemplate = "%artist% - %title%",
            CopyTags = true,
            CopyCover = true
        };

        var (result, outputPath) = await ConvertAsync(paths, logger, workspace, source, preset, new ConversionEncoderPaths { FlacPath = flacPath });

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(outputPath));
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(outputPath), 0, 4));

        var tags = new TagLibTagService(logger).Read(outputPath);
        Assert.Equal("Test Title", tags.Title);
        Assert.Equal("Test Artist", tags.Artist);
        Assert.Equal("Test Composer", tags.Composer);
    }

    [Fact]
    public async Task Convert_WavToMp3_UsesLameAndCopiesTags()
    {
        var lamePath = ResolveTool("lame.exe");
        if (lamePath is null)
        {
            Assert.Skip("tools\\lame.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var source = TestAudio.CreateWav(workspace.Combine("source.wav"));
        WriteTags(logger, source);

        var preset = new ConversionPreset
        {
            Name = "mp3",
            Format = ConversionFormat.Mp3,
            RateMode = ConversionRateMode.Cbr,
            BitrateKbps = 320,
            BitDepth = ConversionBitDepth.Pcm16,
            FileNameTemplate = "%title%",
            CopyTags = true
        };

        var (result, outputPath) = await ConvertAsync(paths, logger, workspace, source, preset, new ConversionEncoderPaths { LamePath = lamePath });

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(outputPath));
        Assert.True(new FileInfo(outputPath).Length > 1000);

        var tags = new TagLibTagService(logger).Read(outputPath);
        Assert.Equal("Test Title", tags.Title);
        Assert.Equal("Test Artist", tags.Artist);
    }

    [Fact]
    public async Task Convert_WavToWav_ResamplesWithSsrc()
    {
        var ssrcPath = ResolveTool("ssrc.exe");
        if (ssrcPath is null)
        {
            Assert.Skip("tools\\ssrc.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var source = TestAudio.CreateWav(workspace.Combine("source.wav"), seconds: 1, sampleRate: 44100);

        var preset = new ConversionPreset
        {
            Name = "wav-48k",
            Format = ConversionFormat.Wav,
            BitDepth = ConversionBitDepth.Pcm16,
            Resample = true,
            TargetSampleRate = 48000,
            FileNameTemplate = "%title%",
            CopyTags = false
        };

        var (result, outputPath) = await ConvertAsync(paths, logger, workspace, source, preset, new ConversionEncoderPaths { SsrcPath = ssrcPath });

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(outputPath));

        using var converted = TagLib.File.Create(outputPath);
        Assert.Equal(48000, converted.Properties.AudioSampleRate);
        Assert.InRange(converted.Properties.Duration.TotalSeconds, 0.8, 1.3);
    }

    [Fact]
    public async Task Convert_FlacToWav_DecodesMediaFoundationSource()
    {
        var flacPath = ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var sourceWav = TestAudio.CreateWav(workspace.Combine("source.wav"), seconds: 1);
        var flacSource = workspace.Combine("source.flac");
        var flacResult = await new ExternalRunnerProbe().RunAsync(
            flacPath,
            ["-5", "-o", flacSource, sourceWav]);
        Assert.True(flacResult, "flac.exe failed to create the test source.");

        var preset = new ConversionPreset
        {
            Name = "wav",
            Format = ConversionFormat.Wav,
            BitDepth = ConversionBitDepth.Pcm16,
            FileNameTemplate = "%title%",
            CopyTags = false
        };

        var (result, outputPath) = await ConvertAsync(paths, logger, workspace, flacSource, preset, new ConversionEncoderPaths());

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(outputPath));
        using var converted = TagLib.File.Create(outputPath);
        Assert.InRange(converted.Properties.Duration.TotalSeconds, 0.8, 1.3);
    }

    private static async Task<(ConversionResult Result, string OutputPath)> ConvertAsync(
        AppPaths paths,
        IAppLogger logger,
        TempWorkspace workspace,
        string sourcePath,
        ConversionPreset preset,
        ConversionEncoderPaths encoders)
    {
        var converter = new AudioConverter(new TagLibTagService(logger), logger, paths);

        var outputDirectory = workspace.CreateFolder("out");
        var outputPath = Path.Combine(
            outputDirectory,
            Path.GetFileNameWithoutExtension(sourcePath) + ConversionNaming.GetExtension(preset.Format));

        var result = await converter.ConvertAsync(
            new ConversionRequest(sourcePath, outputPath, preset, encoders),
            progress: null);

        return (result, outputPath);
    }

    private static void WriteTags(IAppLogger logger, string path)
    {
        new TagLibTagService(logger).Write(path, new TagEditModel
        {
            Title = "Test Title",
            Artist = "Test Artist",
            Album = "Test Album",
            AlbumArtist = "Test AlbumArtist",
            Composer = "Test Composer",
            Genre = "Test Genre",
            Comment = "Test Comment",
            Year = 2024,
            TrackNumber = 3,
            DiscNumber = 1
        });
    }

    private sealed class ExternalRunnerProbe
    {
        public async Task<bool> RunAsync(string executable, IReadOnlyList<string> arguments)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
    }
}


