using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;
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
            var intermediateWav = Path.Combine(tempDirectory, "decoded.wav");
            ConversionPreset effectivePreset;
            try
            {
                var sourceSampleRate = decoded.Provider.WaveFormat.SampleRate;
                effectivePreset = ResolveEffectivePreset(preset, sourceSampleRate, ref warning);
                var sourceBits = decoded.SourceBitsPerSample > 0
                    ? decoded.SourceBitsPerSample
                    : ProbeBitsPerSample(sourcePath);
                var intermediateBits = ResolveBitDepth(effectivePreset, sourceBits);

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
                        decoded.Provider,
                        effectivePreset,
                        intermediateBits,
                        ssrcPath,
                        tempDirectory,
                        sourcePath,
                        cancellationToken).ConfigureAwait(false);
                    intermediateWav = resampled.Path;
                    warning = AppendWarning(warning, resampled.Warning);
                }
                else
                {
                    Report(ConversionStage.Resampling, 0.3);
                    PcmWavWriter.Write(decoded.Provider, intermediateWav, intermediateBits, cancellationToken);
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

    private async Task<ResampleOutcome> ResampleAsync(
        NAudio.Wave.ISampleProvider source,
        ConversionPreset preset,
        int bitDepth,
        string? ssrcPath,
        string tempDirectory,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var resampledPath = Path.Combine(tempDirectory, "resampled.wav");
        if (ssrcPath is not null)
        {
            var preResamplePath = Path.Combine(tempDirectory, "pre-resample.wav");
            var preResampleFrames = PcmWavWriter.Write(source, preResamplePath, bitDepth, cancellationToken);

            var arguments = EncoderCommandLine.BuildSsrcArgs(preset, preResamplePath, resampledPath, bitDepth);
            _logger.Debug($"Conversion resample: {EncoderCommandLine.FormatCommandLine(ssrcPath, arguments)}");
            var toolResult = await _runner.RunAsync(ssrcPath, arguments, cancellationToken).ConfigureAwait(false);
            if (toolResult.Succeeded && File.Exists(resampledPath) && new FileInfo(resampledPath).Length > 0)
            {
                // SSRC flushes its filter tail as trailing silence; trim back to the expected length.
                var expectedFrames = (long)Math.Round(
                    preResampleFrames * (double)preset.TargetSampleRate / source.WaveFormat.SampleRate,
                    MidpointRounding.AwayFromZero);
                PcmWavWriter.TrimToFrames(resampledPath, expectedFrames);
                return new ResampleOutcome(resampledPath, null);
            }

            _logger.Warn($"SSRC resampling failed for {sourcePath} (exit {toolResult.ExitCode}): {Truncate(toolResult.StandardError)}");
            return new ResampleOutcome(resampledPath, "SSRC 重采样失败，已回退到内置重采样器。");
        }

        var resampler = new WdlResamplingSampleProvider(source, preset.TargetSampleRate);
        PcmWavWriter.Write(resampler, resampledPath, bitDepth, cancellationToken);
        return new ResampleOutcome(resampledPath, null);
    }

    private static ConversionPreset ResolveEffectivePreset(
        ConversionPreset preset,
        int sourceSampleRate,
        ref string? warning)
    {
        if (preset.Resample && preset.TargetSampleRate > 0)
        {
            return preset;
        }

        var forcedRate = preset.Format switch
        {
            ConversionFormat.Mp3 when sourceSampleRate > 48000 => 48000,
            ConversionFormat.Aac when sourceSampleRate > 96000 => 96000,
            _ => 0
        };

        if (forcedRate <= 0)
        {
            return preset;
        }

        var effective = preset.Clone();
        effective.Resample = true;
        effective.TargetSampleRate = forcedRate;
        var formatLabel = preset.Format == ConversionFormat.Mp3 ? "MP3" : "AAC";
        warning = AppendWarning(warning, $"{formatLabel} 不支持 {sourceSampleRate} Hz，已自动重采样到 {forcedRate} Hz。");
        return effective;
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
        _tagService.Write(outputPath, new TagEditModel
        {
            Title = tags.Title,
            Artist = tags.Artist,
            Album = tags.Album,
            AlbumArtist = tags.AlbumArtist,
            Composer = tags.Composer,
            Genre = tags.Genre,
            Comment = tags.Comment,
            Year = tags.Year ?? 0,
            TrackNumber = tags.TrackNumber,
            DiscNumber = tags.DiscNumber,
            NewCoverBytes = copyCover ? tags.CoverBytes : null,
            NewCoverMime = copyCover ? tags.CoverMime : null,
            RemoveCover = false
        });
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
