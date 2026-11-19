using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Conversion;
using EchoGarage.Infrastructure.Logging;
using EchoGarage.Infrastructure.Tagging;
using NAudio.Wave;

namespace EchoGarage.Tests;

/// <summary>
/// End-to-end conversion tests that exercise the bundled tools in <c>tools\</c>.
/// They are skipped when the tools are missing (e.g. a source-only checkout).
/// </summary>
public sealed class ConversionPipelineTests
{
    [Fact]
    public async Task Convert_WavToFlac_CopiesTagsAndProducesFlacFile()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
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
        var lamePath = TestTools.ResolveTool("lame.exe");
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
        var ssrcPath = TestTools.ResolveTool("ssrc.exe");
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
    public async Task Convert_WavToWav_ResamplesWithSsrc_PreservesAudioAtStart()
    {
        var ssrcPath = TestTools.ResolveTool("ssrc.exe");
        if (ssrcPath is null)
        {
            Assert.Skip("tools\\ssrc.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        // 96 kHz -> 48 kHz exercises the downsample path whose SSRC latency is largest.
        var source = TestAudio.CreateWav(
            workspace.Combine("source.wav"),
            seconds: 1,
            sampleRate: 96000,
            frequency: 440);

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

        var (result, outputPath) = await ConvertAsync(
            paths,
            logger,
            workspace,
            source,
            preset,
            new ConversionEncoderPaths { SsrcPath = ssrcPath });

        Assert.True(result.Success, result.Error);
        var samples = ReadMonoSamples(outputPath);
        // The expected output length must not include SSRC's leading/trailing padding.
        Assert.Equal(48000, samples.Length);

        // Regression: before latency compensation the first ~0.34 s were SSRC padding
        // and the 440 Hz tone only started afterwards.
        var window = samples.Take(4800).ToArray();
        var rms = Math.Sqrt(window.Sum(sample => sample * sample) / window.Length);
        Assert.InRange(rms, 0.12, 0.22);

        // The retained window must be phase-aligned with the original 440 Hz tone.
        double xy = 0, xx = 0, yy = 0;
        for (var n = 0; n < window.Length; n++)
        {
            var expected = Math.Sin(2 * Math.PI * 440 * n / 48000.0);
            xy += window[n] * expected;
            xx += window[n] * window[n];
            yy += expected * expected;
        }

        var correlation = xy / Math.Sqrt(xx * yy);
        Assert.True(correlation > 0.9, $"expected a phase-aligned 440 Hz tone, correlation was {correlation:0.###}.");
    }

    [Fact]
    public async Task Convert_FlacToWav_DecodesMediaFoundationSource()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
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
        var flacResult = await TestTools.RunAsync(
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

    [Fact]
    public void TryProbeSampleRate_ReadsWavAndReturnsNullForMissingFile()
    {
        using var workspace = new TempWorkspace();
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        var logger = new FileLogger(paths);
        var source = TestAudio.CreateWav(workspace.Combine("source.wav"), sampleRate: 96000);
        var converter = new AudioConverter(new TagLibTagService(logger), logger, paths);

        Assert.Equal(96000, converter.TryProbeSampleRate(source));
        Assert.Null(converter.TryProbeSampleRate(workspace.Combine("missing.wav")));
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

    private static float[] ReadMonoSamples(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var buffer = new float[4096];
        var samples = new List<float>();
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                samples.Add(buffer[i]);
            }
        }

        return samples.ToArray();
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
}


