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
/// 转换增益与 16-bit TPDF dither 的端到端测试（ADR-028 §3.6 / §3.6.1）：
/// 走真实 <see cref="AudioConverter"/> 管线，验证 WAV/FLAC 输出幅度、标签保留、源文件不变、
/// dither 开关只影响 16-bit 输出且误差受控。
/// </summary>
public sealed class GainConversionTests
{
    [Fact]
    public async Task Convert_WavToWav_AppliesGainDb_KeepsTagsAndLeavesSourceUntouched()
    {
        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(
            workspace.Combine("source.wav"),
            channels: 2,
            sampleRate: 44100,
            bitDepth: 16,
            seconds: 2,
            amplitudes: [0.2, 0.2]);
        new TagLibTagService(logger).Write(source, new TagEditModel { Title = "Test Title", Artist = "Test Artist" });
        var sourceHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source));
        var sourceRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(source));

        var output = workspace.Combine("gained.wav");
        var result = await ConvertAsync(
            paths,
            logger,
            source,
            output,
            CreateWavPreset(copyTags: true),
            gainDb: 6.0);

        Assert.True(result.Success, result.Error);
        var outputRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(output));
        var ratio = outputRms / sourceRms;
        Assert.InRange(ratio, 1.95, 2.04); // 10^(6/20) ≈ 1.995

        var tags = new TagLibTagService(logger).Read(output);
        Assert.Equal("Test Title", tags.Title);
        Assert.Equal("Test Artist", tags.Artist);

        Assert.Equal(sourceHash, System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public async Task Convert_WavToWav_GainDbZero_MatchesLegacyRequest()
    {
        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 2, seconds: 1, amplitudes: [0.3, 0.3]);
        var preset = CreateWavPreset();

        // 旧调用方式：4 个位置参数，不指定增益/dither。
        var legacy = await ConvertRequestAsync(paths, logger, new ConversionRequest(source, workspace.Combine("legacy.wav"), preset, new ConversionEncoderPaths()));
        // 新调用方式：显式 GainDb=0、EnableDither=false，必须与旧行为逐位一致。
        var explicitZero = await ConvertRequestAsync(paths, logger, new ConversionRequest(source, workspace.Combine("explicit.wav"), preset, new ConversionEncoderPaths(), 0.0, false));

        Assert.True(legacy.Success, legacy.Error);
        Assert.True(explicitZero.Success, explicitZero.Error);
        Assert.Equal(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(workspace.Combine("legacy.wav"))),
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(workspace.Combine("explicit.wav"))));
    }

    [Fact]
    public async Task Convert_WavToWav_DitherChanges16BitOutputWithinTwoLsb()
    {
        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 2, seconds: 2, amplitudes: [0.25, 0.25]);
        var plainPath = workspace.Combine("plain.wav");
        var ditheredPath = workspace.Combine("dithered.wav");

        var plain = await ConvertAsync(paths, logger, source, plainPath, CreateWavPreset(), enableDither: false);
        var dithered = await ConvertAsync(paths, logger, source, ditheredPath, CreateWavPreset(), enableDither: true);

        Assert.True(plain.Success, plain.Error);
        Assert.True(dithered.Success, dithered.Error);
        var plainSamples = TestPcmAudio.ReadInt16Samples(plainPath);
        var ditheredSamples = TestPcmAudio.ReadInt16Samples(ditheredPath);
        Assert.Equal(plainSamples.Length, ditheredSamples.Length);

        var differing = 0;
        var maxDelta = 0;
        for (var index = 0; index < plainSamples.Length; index++)
        {
            var delta = Math.Abs(plainSamples[index] - ditheredSamples[index]);
            if (delta != 0)
            {
                differing++;
            }

            maxDelta = Math.Max(maxDelta, delta);
        }

        Assert.True(differing > plainSamples.Length / 4, $"dither 只改变了 {differing} 个采样，疑似未生效。");
        Assert.True(maxDelta <= 2, $"dither 误差达到 {maxDelta} LSB，超过 TPDF 的 1 LSB 峰值范围。");

        // dither 只是量化误差，不应明显改变信号幅度。
        var plainRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(plainPath));
        var ditheredRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(ditheredPath));
        Assert.InRange(ditheredRms / plainRms, 0.995, 1.005);
    }

    [Fact]
    public async Task Convert_WavTo24Bit_DitherHasNoEffect()
    {
        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 2, seconds: 1, amplitudes: [0.25, 0.25]);
        var preset = CreateWavPreset(ConversionBitDepth.Pcm24);
        var plainPath = workspace.Combine("plain24.wav");
        var ditheredPath = workspace.Combine("dithered24.wav");

        var plain = await ConvertAsync(paths, logger, source, plainPath, preset, enableDither: false);
        var dithered = await ConvertAsync(paths, logger, source, ditheredPath, preset, enableDither: true);

        Assert.True(plain.Success, plain.Error);
        Assert.True(dithered.Success, dithered.Error);
        using var reader = new WaveFileReader(plainPath);
        Assert.Equal(24, reader.WaveFormat.BitsPerSample);
        Assert.Equal(File.ReadAllBytes(plainPath), File.ReadAllBytes(ditheredPath));
    }

    [Fact]
    public async Task Convert_WavToFlac_AppliesGain_WhenFlacAvailable()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 1, seconds: 2, amplitudes: [0.2]);
        var sourceRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(source));
        var output = workspace.Combine("gained.flac");

        var result = await ConvertAsync(
            paths,
            logger,
            source,
            output,
            CreateFlacPreset(),
            gainDb: 6.0,
            encoders: new ConversionEncoderPaths { FlacPath = flacPath });

        Assert.True(result.Success, result.Error);
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 4));

        var decoded = workspace.Combine("decoded.wav");
        Assert.True(await TestTools.RunAsync(flacPath, ["-d", "-f", "-s", "-o", decoded, output]), "flac 解码失败。");
        var ratio = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(decoded)) / sourceRms;
        Assert.InRange(ratio, 1.95, 2.04);
    }

    [Fact]
    public async Task Convert_WavToFlac_DitherChanges16BitOutput_WhenFlacAvailable()
    {
        var flacPath = TestTools.ResolveTool("flac.exe");
        if (flacPath is null)
        {
            Assert.Skip("tools\\flac.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 1, seconds: 2, amplitudes: [0.25]);
        var encoders = new ConversionEncoderPaths { FlacPath = flacPath };

        var plainFlac = workspace.Combine("plain.flac");
        var ditheredFlac = workspace.Combine("dithered.flac");
        var plainResult = await ConvertAsync(paths, logger, source, plainFlac, CreateFlacPreset(), enableDither: false, encoders: encoders);
        var ditheredResult = await ConvertAsync(paths, logger, source, ditheredFlac, CreateFlacPreset(), enableDither: true, encoders: encoders);
        Assert.True(plainResult.Success, plainResult.Error);
        Assert.True(ditheredResult.Success, ditheredResult.Error);

        var plainWav = workspace.Combine("plain-decoded.wav");
        var ditheredWav = workspace.Combine("dithered-decoded.wav");
        Assert.True(await TestTools.RunAsync(flacPath, ["-d", "-f", "-s", "-o", plainWav, plainFlac]));
        Assert.True(await TestTools.RunAsync(flacPath, ["-d", "-f", "-s", "-o", ditheredWav, ditheredFlac]));

        var plainSamples = TestPcmAudio.ReadInt16Samples(plainWav);
        var ditheredSamples = TestPcmAudio.ReadInt16Samples(ditheredWav);
        Assert.Equal(plainSamples.Length, ditheredSamples.Length);

        var differing = 0;
        var maxDelta = 0;
        for (var index = 0; index < plainSamples.Length; index++)
        {
            var delta = Math.Abs(plainSamples[index] - ditheredSamples[index]);
            if (delta != 0)
            {
                differing++;
            }

            maxDelta = Math.Max(maxDelta, delta);
        }

        Assert.True(differing > plainSamples.Length / 4, $"FLAC 输出的 dither 只改变了 {differing} 个采样。");
        Assert.True(maxDelta <= 2, $"FLAC 输出 dither 误差达到 {maxDelta} LSB。");
    }

    [Fact]
    public async Task Convert_ResampledWithGain_AppliesGain_WhenSsrcAvailable()
    {
        var ssrcPath = TestTools.ResolveTool("ssrc.exe");
        if (ssrcPath is null)
        {
            Assert.Skip("tools\\ssrc.exe is not available.");
        }

        using var workspace = new TempWorkspace();
        var paths = CreatePaths(workspace);
        var logger = new FileLogger(paths);
        var source = TestPcmAudio.WriteSineWav(workspace.Combine("source.wav"), channels: 2, seconds: 2, amplitudes: [0.2, 0.2]);
        var preset = CreateWavPreset(resample: true, targetSampleRate: 48000);
        var encoders = new ConversionEncoderPaths { SsrcPath = ssrcPath };
        var plainOutput = workspace.Combine("plain-48k.wav");
        var gainedOutput = workspace.Combine("gained-48k.wav");

        // SSRC 的高质量 profile 有固定的滤波器前导延迟，TrimToFrames 只裁尾部；因此不比对绝对幅度，
        // 而是比较“同样重采样、只有增益不同”的两份输出，前导延迟的影响会被抵消。
        var plain = await ConvertAsync(paths, logger, source, plainOutput, preset, gainDb: 0, encoders: encoders);
        var gained = await ConvertAsync(paths, logger, source, gainedOutput, preset, gainDb: 6.0, encoders: encoders);
        Assert.True(plain.Success, plain.Error);
        Assert.True(gained.Success, gained.Error);

        foreach (var outputPath in new[] { plainOutput, gainedOutput })
        {
            using var reader = new WaveFileReader(outputPath);
            Assert.Equal(48000, reader.WaveFormat.SampleRate);
        }

        var plainRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(plainOutput));
        var gainedRms = TestPcmAudio.RootMeanSquare(TestPcmAudio.ReadFloatSamples(gainedOutput));
        Assert.InRange(gainedRms / plainRms, 1.95, 2.04); // 10^(6/20) ≈ 1.995
    }

    private static AppPaths CreatePaths(TempWorkspace workspace)
    {
        var paths = new AppPaths(workspace.Combine("state"));
        paths.EnsureCreated();
        return paths;
    }

    private static ConversionPreset CreateWavPreset(
        ConversionBitDepth bitDepth = ConversionBitDepth.Pcm16,
        bool copyTags = false,
        bool resample = false,
        int targetSampleRate = 0) => new()
    {
        Name = "wav-test",
        Format = ConversionFormat.Wav,
        BitDepth = bitDepth,
        CopyTags = copyTags,
        CopyCover = false,
        FileNameTemplate = "%title%",
        Resample = resample,
        TargetSampleRate = targetSampleRate
    };

    private static ConversionPreset CreateFlacPreset() => new()
    {
        Name = "flac-test",
        Format = ConversionFormat.Flac,
        BitDepth = ConversionBitDepth.Source,
        Quality = 5,
        CopyTags = false,
        CopyCover = false,
        FileNameTemplate = "%title%"
    };

    private static Task<ConversionResult> ConvertAsync(
        AppPaths paths,
        IAppLogger logger,
        string sourcePath,
        string outputPath,
        ConversionPreset preset,
        double gainDb = 0,
        bool enableDither = false,
        ConversionEncoderPaths? encoders = null) =>
        ConvertRequestAsync(
            paths,
            logger,
            new ConversionRequest(sourcePath, outputPath, preset, encoders ?? new ConversionEncoderPaths(), gainDb, enableDither));

    private static async Task<ConversionResult> ConvertRequestAsync(AppPaths paths, IAppLogger logger, ConversionRequest request)
    {
        var converter = new AudioConverter(new TagLibTagService(logger), logger, paths);
        return await converter.ConvertAsync(request, progress: null);
    }
}
