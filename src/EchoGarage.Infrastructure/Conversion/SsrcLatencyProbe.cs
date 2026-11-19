using System.Collections.Concurrent;
using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using NAudio.Wave;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>
/// Measures the leading padding SSRC inserts into resampled WAV output.
/// SSRC uses a linear-phase FIR; its output starts with the filter group delay (and is
/// also padded at the tail). The conversion pipeline trims the tail to the expected frame
/// count, so without removing the leading padding the audio content shifts later and the
/// end of the track is cut off.
/// </summary>
internal static class SsrcLatencyProbe
{
    /// <summary>Impulse position in the probe WAV; far enough from the start to avoid SSRC's initial transient handling.</summary>
    private const int ImpulseFrame = 5000;

    private const double MinimumPeak = 0.05;

    private static readonly ConcurrentDictionary<LatencyKey, Lazy<Task<int>>> Cache = new();

    /// <summary>
    /// Returns the number of leading frames SSRC adds for the given rate/profile pair.
    /// Results are cached per pair; a failed probe is retried on the next request.
    /// </summary>
    public static async Task<int> MeasureAsync(
        string ssrcPath,
        int sourceSampleRate,
        int targetSampleRate,
        ConversionResampleProfile profile,
        string tempDirectory,
        ExternalEncoderRunner runner,
        IAppLogger logger)
    {
        var key = new LatencyKey(sourceSampleRate, targetSampleRate, profile);
        var lazy = Cache.GetOrAdd(
            key,
            _ => new Lazy<Task<int>>(() => MeasureCoreAsync(
                ssrcPath,
                key,
                tempDirectory,
                runner,
                logger)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Do not cache failures; a transient I/O error should not break later conversions.
            Cache.TryRemove(key, out _);
            throw;
        }
    }

    private static async Task<int> MeasureCoreAsync(
        string ssrcPath,
        LatencyKey key,
        string tempDirectory,
        ExternalEncoderRunner runner,
        IAppLogger logger)
    {
        var probeDirectory = Path.Combine(tempDirectory, "ssrc-latency", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(probeDirectory);
        try
        {
            var probeFrames = Math.Max(key.SourceRate, ImpulseFrame + key.SourceRate / 2);
            var probePath = Path.Combine(probeDirectory, "probe.wav");
            var outputPath = Path.Combine(probeDirectory, "probe-out.wav");
            WriteImpulseProbe(probePath, key.SourceRate, probeFrames);

            var arguments = EncoderCommandLine.BuildSsrcArgs(
                key.TargetRate,
                key.Profile,
                probePath,
                outputPath,
                16);
            var toolResult = await runner.RunAsync(ssrcPath, arguments, CancellationToken.None).ConfigureAwait(false);
            if (!toolResult.Succeeded || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidOperationException(
                    $"SSRC 延迟探测失败（exit {toolResult.ExitCode}）：{toolResult.StandardError.Trim()}");
            }

            var (peakFrame, peak) = FindPeak(outputPath);
            if (peak < MinimumPeak)
            {
                throw new InvalidOperationException("SSRC 延迟探测输出中没有可识别的脉冲。");
            }

            var expectedFrame = ImpulseFrame * (double)key.TargetRate / key.SourceRate;
            var latencyFrames = (int)Math.Round(peakFrame - expectedFrame, MidpointRounding.AwayFromZero);
            if (latencyFrames < 0 || latencyFrames > key.TargetRate * 2)
            {
                throw new InvalidOperationException($"SSRC 延迟探测结果异常：{latencyFrames} 帧。");
            }

            logger.Debug(
                $"SSRC latency probe {key.SourceRate} Hz -> {key.TargetRate} Hz ({key.Profile}): " +
                $"{latencyFrames} leading frames (impulse peak at {peakFrame}).");
            return latencyFrames;
        }
        finally
        {
            TryDeleteDirectory(probeDirectory);
        }
    }

    private static void WriteImpulseProbe(string path, int sampleRate, int frames)
    {
        var format = new WaveFormat(sampleRate, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        var buffer = new byte[format.BlockAlign * 4096];
        var frameIndex = 0;
        while (frameIndex < frames)
        {
            var count = Math.Min(frames - frameIndex, buffer.Length / format.BlockAlign);
            Array.Clear(buffer, 0, count * format.BlockAlign);
            if (ImpulseFrame >= frameIndex && ImpulseFrame < frameIndex + count)
            {
                var offset = (ImpulseFrame - frameIndex) * format.BlockAlign;
                buffer[offset] = 0xFF;
                buffer[offset + 1] = 0x7F; // short.MaxValue, little-endian
            }

            writer.Write(buffer, 0, count * format.BlockAlign);
            frameIndex += count;
        }
    }

    private static (int PeakFrame, double Peak) FindPeak(string path)
    {
        using var reader = new WaveFileReader(path);
        var format = reader.WaveFormat;
        if (format.BitsPerSample != 16 || format.Channels != 1)
        {
            throw new InvalidOperationException(
                $"SSRC 延迟探测输出格式异常：{format.BitsPerSample}-bit / {format.Channels} 声道。");
        }

        var buffer = new byte[format.BlockAlign * 4096];
        var frameOffset = 0;
        var peakFrame = 0;
        double peak = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            var frameCount = read / format.BlockAlign;
            for (var i = 0; i < frameCount; i++)
            {
                var offset = i * format.BlockAlign;
                var value = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                var magnitude = Math.Abs(value / 32768.0);
                if (magnitude > peak)
                {
                    peak = magnitude;
                    peakFrame = frameOffset + i;
                }
            }

            frameOffset += frameCount;
        }

        return (peakFrame, peak);
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
            // Best-effort cleanup; the conversion temp folder is cleaned up separately.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the conversion temp folder is cleaned up separately.
        }
    }

    private readonly record struct LatencyKey(
        int SourceRate,
        int TargetRate,
        ConversionResampleProfile Profile);

    // expose for tests? Probe is internal and exercised through conversion integration tests.
}
