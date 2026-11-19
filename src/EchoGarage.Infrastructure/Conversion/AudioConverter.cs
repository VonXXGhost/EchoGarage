using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>
/// Decodes a source file to PCM, optionally resamples it (SSRC with a WDL fallback),
/// runs the external encoder and copies tags/cover art.
/// </summary>
public sealed class AudioConverter : IAudioConverter
{
    private const string TempFolderName = "Conversion";

    private readonly ITagService _tagService;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly EncoderLocator _locator;
    private readonly ExternalEncoderRunner _runner = new();

    public AudioConverter(ITagService tagService, IAppLogger logger, AppPaths paths)
    {
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _locator = new EncoderLocator();
    }

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = request.SourcePath;
        var outputPath = request.OutputPath;
        var tempDirectory = Path.Combine(_paths.DataDirectory, TempFolderName, Guid.NewGuid().ToString("N"));
        string? warning = null;

        void Report(ConversionStage stage, double fraction, string? detail = null)
        {
            progress?.Report(new ConversionProgress(0, 0, sourcePath, stage, fraction, detail));
        }

        try
        {
            if (!File.Exists(sourcePath))
            {
                return ConversionResult.Fail("源文件不存在。");
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                return ConversionResult.Fail("无法确定输出文件夹。");
            }

            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(tempDirectory);
            Report(ConversionStage.Preparing, 0.02);

            var preset = request.Preset;
            string? encoderPath = null;
            if (preset.Format != ConversionFormat.Wav)
            {
                encoderPath = _locator.Locate(preset.Format, request.Encoders);
                if (encoderPath is null)
                {
                    return ConversionResult.Fail(DescribeMissingEncoder(preset.Format));
                }
            }

            Report(ConversionStage.Decoding, 0.1);
            var decoded = MediaDecoder.Open(sourcePath);
            // ADR-028 §3.6：响度调整的线性增益在解码之后、重采样/写中间 WAV 之前施加。
            // GainDb = 0 时不包装，保证旧调用（4 参 ConversionRequest）行为逐位不变。
            ISampleProvider decodedProvider = request.GainDb == 0.0
                ? decoded.Provider
                : new GainSampleProvider(decoded.Provider, request.GainDb);
            var intermediateWav = Path.Combine(tempDirectory, "decoded.wav");
            // 有损格式的降采样由调用方在转换前询问用户（ConversionCoordinator），这里只执行预设自己的要求。
            var effectivePreset = preset;
            try
            {
                var sourceSampleRate = decodedProvider.WaveFormat.SampleRate;
                var sourceBits = decoded.SourceBitsPerSample > 0
                    ? decoded.SourceBitsPerSample
                    : ProbeBitsPerSample(sourcePath);
                var intermediateBits = ResolveBitDepth(effectivePreset, sourceBits);
                // ADR-028 §3.6.1：只有 16-bit 无损输出（Flac/WAV）才加 TPDF dither，
                // 24-bit、32f 与有损格式（编码器自己处理量化）都不加。
                var enableDither = request.EnableDither &&
                    intermediateBits <= 16 &&
                    effectivePreset.Format is ConversionFormat.Flac or ConversionFormat.Wav;

                if (effectivePreset.Resample &&
                    effectivePreset.TargetSampleRate > 0 &&
                    effectivePreset.TargetSampleRate != sourceSampleRate)
                {
                    var ssrcPath = _locator.Locate(ConversionTool.Ssrc, request.Encoders.SsrcPath);
                    if (ssrcPath is null)
                    {
                        warning = AppendWarning(warning, "未找到 SSRC，已回退到内置重采样器。");
                    }

                    Report(ConversionStage.Resampling, 0.25);
                    var resampled = await ResampleAsync(
                        decodedProvider,
                        effectivePreset,
                        intermediateBits,
                        ssrcPath,
                        tempDirectory,
                        sourcePath,
                        enableDither,
                        cancellationToken).ConfigureAwait(false);
                    intermediateWav = resampled.Path;
                    warning = AppendWarning(warning, resampled.Warning);
                }
                else
                {
                    Report(ConversionStage.Resampling, 0.3);
                    PcmWavWriter.Write(
                        decodedProvider,
                        intermediateWav,
                        intermediateBits,
                        cancellationToken,
                        enableDither);
                }
            }
            finally
            {
                decoded.Owner.Dispose();
            }

            Report(ConversionStage.Encoding, 0.55);
            var encodedPath = Path.Combine(tempDirectory, "encoded" + ConversionNaming.GetExtension(effectivePreset.Format));
            if (File.Exists(encodedPath))
            {
                File.Delete(encodedPath);
            }

            if (effectivePreset.Format == ConversionFormat.Wav)
            {
                File.Move(intermediateWav, encodedPath, overwrite: true);
            }
            else
            {
                var arguments = effectivePreset.Format switch
                {
                    ConversionFormat.Flac => EncoderCommandLine.BuildFlacArgs(effectivePreset, intermediateWav, encodedPath, overwrite: true),
                    ConversionFormat.Mp3 => EncoderCommandLine.BuildLameArgs(effectivePreset, intermediateWav, encodedPath),
                    ConversionFormat.Aac => EncoderCommandLine.BuildNeroAacArgs(effectivePreset, intermediateWav, encodedPath),
                    _ => throw new InvalidOperationException($"Unsupported conversion format: {effectivePreset.Format}")
                };

                _logger.Debug($"Conversion encode: {EncoderCommandLine.FormatCommandLine(encoderPath!, arguments)}");
                var toolResult = await _runner.RunAsync(encoderPath!, arguments, cancellationToken).ConfigureAwait(false);
                if (!toolResult.Succeeded)
                {
                    var details = string.IsNullOrWhiteSpace(toolResult.StandardError)
                        ? toolResult.StandardOutput
                        : toolResult.StandardError;
                    return ConversionResult.Fail($"编码器返回错误（{toolResult.ExitCode}）：{Truncate(details)}");
                }

                if (!File.Exists(encodedPath) || new FileInfo(encodedPath).Length == 0)
                {
                    return ConversionResult.Fail("编码器没有生成输出文件。");
                }
            }

            Report(ConversionStage.Encoding, 0.85);

            if (effectivePreset.CopyTags)
            {
                Report(ConversionStage.WritingTags, 0.9);
                try
                {
                    CopyTags(sourcePath, encodedPath, effectivePreset.CopyCover);
                }
                catch (Exception exception)
                {
                    warning = AppendWarning(warning, "标签复制失败：" + exception.Message);
                    _logger.Warn($"Copying tags failed for {outputPath}: {exception.Message}");
                }
            }

            File.Move(encodedPath, outputPath, overwrite: true);
            Report(ConversionStage.Done, 1.0);
            _logger.Info($"Converted {sourcePath} -> {outputPath} ({effectivePreset.Format}).");
            return ConversionResult.Ok(outputPath, warning);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error($"Conversion failed: {sourcePath} -> {outputPath}", exception);
            return ConversionResult.Fail(exception.Message);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>Removes conversion temp folders left behind by a previous crash.</summary>
    public static void CleanupStaleTempFolders(AppPaths paths, IAppLogger logger)
    {
        var root = Path.Combine(paths.DataDirectory, TempFolderName);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception)
            {
                logger.Warn($"Failed to clean conversion temp folder {directory}: {exception.Message}");
            }
        }
    }

    /// <inheritdoc />
    public int? TryProbeSampleRate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(path);
            var sampleRate = file.Properties.AudioSampleRate;
            if (sampleRate > 0)
            {
                return sampleRate;
            }
        }
        catch (Exception exception)
        {
            _logger.Debug($"TagLib sample rate probe failed for {path}: {exception.Message}");
        }

        try
        {
            var decoded = MediaDecoder.Open(path);
            try
            {
                var sampleRate = decoded.Provider.WaveFormat.SampleRate;
                return sampleRate > 0 ? sampleRate : null;
            }
            finally
            {
                decoded.Owner.Dispose();
            }
        }
        catch (Exception exception)
        {
            _logger.Debug($"Media Foundation sample rate probe failed for {path}: {exception.Message}");
            return null;
        }
    }

    private async Task<ResampleOutcome> ResampleAsync(
        ISampleProvider source,
        ConversionPreset preset,
        int bitDepth,
        string? ssrcPath,
        string tempDirectory,
        string sourcePath,
        bool enableDither,
        CancellationToken cancellationToken)
    {
        var resampledPath = Path.Combine(tempDirectory, "resampled.wav");
        if (ssrcPath is not null)
        {
            var preResamplePath = Path.Combine(tempDirectory, "pre-resample.wav");
            // SSRC 只接受整数 PCM；16-bit 目标在第一次量化（pre-resample）时就加 dither。
            var preResampleFrames = PcmWavWriter.Write(
                source,
                preResamplePath,
                bitDepth,
                cancellationToken,
                enableDither);

            var arguments = EncoderCommandLine.BuildSsrcArgs(preset, preResamplePath, resampledPath, bitDepth);
            _logger.Debug($"Conversion resample: {EncoderCommandLine.FormatCommandLine(ssrcPath, arguments)}");
            var toolResult = await _runner.RunAsync(ssrcPath, arguments, cancellationToken).ConfigureAwait(false);
            var expectedFrames = (long)Math.Round(
                preResampleFrames * (double)preset.TargetSampleRate / source.WaveFormat.SampleRate,
                MidpointRounding.AwayFromZero);
            if (toolResult.Succeeded && File.Exists(resampledPath) && new FileInfo(resampledPath).Length > 0)
            {
                try
                {
                    // SSRC pads the beginning with its linear-phase filter delay and the end
                    // with its flush tail. Trim both sides to the expected content window.
                    var latencyFrames = await SsrcLatencyProbe.MeasureAsync(
                        ssrcPath,
                        source.WaveFormat.SampleRate,
                        preset.TargetSampleRate,
                        preset.ResampleProfile,
                        tempDirectory,
                        _runner,
                        _logger).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    PcmWavWriter.TrimToFrames(resampledPath, expectedFrames, latencyFrames);
                    _logger.Debug(
                        $"SSRC resample trimmed {latencyFrames} leading frames and the tail " +
                        $"to {expectedFrames} frames for {sourcePath}.");
                    return new ResampleOutcome(resampledPath, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.Warn($"SSRC latency compensation failed for {sourcePath}: {exception.Message}");
                    ResampleWithWdl(preResamplePath, resampledPath, preset.TargetSampleRate, bitDepth, cancellationToken, enableDither);
                    return new ResampleOutcome(resampledPath, "SSRC 延迟补偿失败，已回退到内置重采样器。");
                }
            }

            _logger.Warn($"SSRC resampling failed for {sourcePath} (exit {toolResult.ExitCode}): {Truncate(toolResult.StandardError)}");
            ResampleWithWdl(preResamplePath, resampledPath, preset.TargetSampleRate, bitDepth, cancellationToken, enableDither);
            return new ResampleOutcome(resampledPath, "SSRC 重采样失败，已回退到内置重采样器。");
        }

        var resampler = new WdlResamplingSampleProvider(source, preset.TargetSampleRate);
        PcmWavWriter.Write(resampler, resampledPath, bitDepth, cancellationToken, enableDither);
        return new ResampleOutcome(resampledPath, null);
    }

    /// <summary>
    /// Resamples the already-quantized intermediate WAV with NAudio's WDL resampler.
    /// Used as a fallback when SSRC fails or its latency cannot be measured.
    /// </summary>
    private static void ResampleWithWdl(
        string sourcePath,
        string outputPath,
        int targetSampleRate,
        int bitDepth,
        CancellationToken cancellationToken,
        bool enableDither)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var reader = new WaveFileReader(sourcePath);
        var provider = reader.ToSampleProvider();
        var resampler = new WdlResamplingSampleProvider(provider, targetSampleRate);
        PcmWavWriter.Write(resampler, outputPath, bitDepth, cancellationToken, enableDither);
    }

    private static int ResolveBitDepth(ConversionPreset preset, int sourceBitsPerSample)
    {
        return preset.BitDepth switch
        {
            ConversionBitDepth.Pcm16 => 16,
            ConversionBitDepth.Pcm24 => 24,
            _ => sourceBitsPerSample > 16 ? 24 : 16
        };
    }

    private static int ProbeBitsPerSample(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var bits = file.Properties.BitsPerSample;
            return bits is > 0 and <= 32 ? bits : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void CopyTags(string sourcePath, string outputPath, bool copyCover)
    {
        var tags = _tagService.Read(sourcePath, includeCover: copyCover);
        _tagService.Write(outputPath, CreateTagCopy(tags, copyCover));
    }

    /// <summary>
    /// Builds the tag write model that copies the source file's tags onto a conversion output.
    /// 单独抽成公开静态方法便于单元测试断言字段传递：尤其 <see cref="TagEditModel.YearText"/>
    /// 必须一并复制，否则 <c>TagLibTagService</c> 会按“清空年份”处理，导致输出文件年份丢失。
    /// </summary>
    public static TagEditModel CreateTagCopy(AudioTagData tags, bool copyCover)
    {
        ArgumentNullException.ThrowIfNull(tags);

        return new TagEditModel
        {
            Title = tags.Title,
            Artist = tags.Artist,
            Album = tags.Album,
            AlbumArtist = tags.AlbumArtist,
            Composer = tags.Composer,
            Genre = tags.Genre,
            Comment = tags.Comment,
            Year = tags.Year ?? 0,
            // 源文件年份/日期原文（如 Vorbis DATE="2025-06-04"、Apple ©day="2025.06.04"），
            // 按输出文件的默认标签类型原样写回；为空时由 TagLibTagService 回退为 4 位年份。
            YearText = tags.YearText,
            YearChanged = true,
            TrackNumber = tags.TrackNumber,
            DiscNumber = tags.DiscNumber,
            NewCoverBytes = copyCover ? tags.CoverBytes : null,
            NewCoverMime = copyCover ? tags.CoverMime : null,
            RemoveCover = false
        };
    }

    private static string DescribeMissingEncoder(ConversionFormat format)
    {
        return format switch
        {
            ConversionFormat.Aac =>
                "未找到 Nero AAC 编码器（neroAacEnc.exe）。Nero AAC 不随软件分发，请在“转换器设置”中指定其路径。",
            ConversionFormat.Flac =>
                "未找到 FLAC 编码器（flac.exe），请检查安装目录下的 tools 文件夹。",
            ConversionFormat.Mp3 =>
                "未找到 LAME 编码器（lame.exe），请检查安装目录下的 tools 文件夹。",
            _ => "未找到编码器。"
        };
    }

    private static string? AppendWarning(string? existing, string? addition)
    {
        if (string.IsNullOrWhiteSpace(addition))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing) ? addition : existing + " " + addition;
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（无详细信息）";
        }

        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : trimmed[..400] + "…";
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort; a stale folder is cleaned on the next start.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed record ResampleOutcome(string Path, string? Warning);
}
