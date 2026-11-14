using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>Writes a float sample provider to a 16-bit or 24-bit PCM WAV file.</summary>
internal static class PcmWavWriter
{
    /// <summary>Writes the provider to a PCM WAV file and returns the number of frames written.</summary>
    public static long Write(ISampleProvider source, string path, int bitDepth, CancellationToken cancellationToken = default)
    {
        return bitDepth <= 16
            ? Write16(source, path, cancellationToken)
            : Write24(source, path, cancellationToken);
    }

    /// <summary>
    /// Truncates a PCM WAV file to the given number of frames. SSRC flushes its filter
    /// tail as trailing silence, so resampled output is trimmed back to the expected length.
    /// </summary>
    public static void TrimToFrames(string path, long maxFrames)
    {
        var trimmedPath = path + ".trim";
        using (var reader = new WaveFileReader(path))
        {
            var maxBytes = maxFrames * reader.WaveFormat.BlockAlign;
            if (maxBytes <= 0 || maxBytes >= reader.Length)
            {
                return;
            }

            using var writer = new WaveFileWriter(trimmedPath, reader.WaveFormat);
            var buffer = new byte[reader.WaveFormat.BlockAlign * 4096];
            long remaining = maxBytes;
            while (remaining > 0)
            {
                var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                writer.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        File.Move(trimmedPath, path, overwrite: true);
    }

    private static long Write16(ISampleProvider source, string path, CancellationToken cancellationToken)
    {
        var format = new WaveFormat(source.WaveFormat.SampleRate, 16, source.WaveFormat.Channels);
        using var writer = new WaveFileWriter(path, format);
        var provider = new SampleToWaveProvider16(source);
        var buffer = new byte[16 * 1024];
        long frames = 0;
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Write(buffer, 0, read);
            frames += read / format.BlockAlign;
        }

        return frames;
    }

    private static long Write24(ISampleProvider source, string path, CancellationToken cancellationToken)
    {
        var format = new WaveFormat(source.WaveFormat.SampleRate, 24, source.WaveFormat.Channels);
        using var writer = new WaveFileWriter(path, format);
        var samples = new float[format.Channels * 4096];
        var bytes = new byte[format.Channels * 4096 * 3];
        long frames = 0;
        int read;
        while ((read = source.Read(samples, 0, samples.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                var sample = Math.Clamp(samples[i], -1f, 1f);
                var value = (int)Math.Round(sample * 8388607f, MidpointRounding.AwayFromZero);
                var offset = i * 3;
                bytes[offset] = (byte)(value & 0xFF);
                bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
                bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
            }

            writer.Write(bytes, 0, read * 3);
            frames += read / format.Channels;
        }

        return frames;
    }
}
