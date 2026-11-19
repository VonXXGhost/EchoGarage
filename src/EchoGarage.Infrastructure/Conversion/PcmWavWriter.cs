using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>Writes a float sample provider to a 16-bit or 24-bit PCM WAV file.</summary>
internal static class PcmWavWriter
{
    /// <summary>1 LSB of 16-bit PCM（TPDF dither 的幅度单位）。</summary>
    private const double Lsb16 = 1.0 / 32768.0;

    /// <summary>
    /// Writes the provider to a PCM WAV file and returns the number of frames written.
    /// </summary>
    /// <param name="source">交错 float32 采样源。</param>
    /// <param name="path">输出 WAV 路径。</param>
    /// <param name="bitDepth">目标位深：≤16 写 16-bit PCM，否则写 24-bit PCM。</param>
    /// <param name="cancellationToken">取消令牌（每次块读写后检查）。</param>
    /// <param name="enableDither">
    /// 是否在 16-bit 量化时加 TPDF dither（ADR-028 §3.6.1）：只在 16-bit 生效，24-bit 忽略；
    /// 关闭时保持与旧实现逐位一致的量化行为。
    /// </param>
    public static long Write(
        ISampleProvider source,
        string path,
        int bitDepth,
        CancellationToken cancellationToken = default,
        bool enableDither = false)
    {
        if (bitDepth <= 16)
        {
            return enableDither
                ? Write16Dithered(source, path, cancellationToken)
                : Write16(source, path, cancellationToken);
        }

        return Write24(source, path, cancellationToken);
    }

    /// <summary>
    /// Trims a PCM WAV file to at most <paramref name="maxFrames"/> frames, optionally
    /// skipping <paramref name="skipFrames"/> leading frames.
    /// SSRC pads both the leading (filter group delay) and trailing (flush) sides of its
    /// output, so resampled files are trimmed back to the expected content window.
    /// </summary>
    public static void TrimToFrames(string path, long maxFrames, long skipFrames = 0)
    {
        var trimmedPath = path + ".trim";
        using (var reader = new WaveFileReader(path))
        {
            var blockAlign = reader.WaveFormat.BlockAlign;
            var startBytes = Math.Max(0, skipFrames) * blockAlign;
            var maxBytes = Math.Max(0, maxFrames) * blockAlign;
            var remaining = Math.Min(maxBytes, reader.Length - startBytes);
            if (remaining <= 0)
            {
                throw new InvalidOperationException(
                    $"重采样输出不足：需要跳过 {skipFrames} 帧并保留 {maxFrames} 帧，" +
                    $"但文件只有 {reader.Length / blockAlign} 帧。");
            }

            if (startBytes == 0 && remaining >= reader.Length)
            {
                return;
            }

            reader.Position = startBytes;
            using var writer = new WaveFileWriter(trimmedPath, reader.WaveFormat);
            var buffer = new byte[blockAlign * 4096];
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

    /// <summary>
    /// 16-bit 量化并在量化前加入 TPDF dither：两个独立均匀 [0,1) 随机数之差 × 1 LSB，
    /// 其噪声约 -93 dBFS（KB-019 §10）。加噪声后再统一 clamp 到 [-1,1] 防止整数溢出。
    /// 与 <see cref="Write16"/> 的定标保持一致（× 32767，AwayFromZero）。
    /// </summary>
    private static long Write16Dithered(ISampleProvider source, string path, CancellationToken cancellationToken)
    {
        var format = new WaveFormat(source.WaveFormat.SampleRate, 16, source.WaveFormat.Channels);
        using var writer = new WaveFileWriter(path, format);
        var samples = new float[format.Channels * 4096];
        var bytes = new byte[format.Channels * 4096 * 2];
        long frames = 0;
        int read;
        while ((read = source.Read(samples, 0, samples.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
            {
                // TPDF：r1 - r2 ∈ (-1, 1)，乘 1 LSB 得到三角分布噪声，再与信号一起量化。
                var dither = (Random.Shared.NextDouble() - Random.Shared.NextDouble()) * Lsb16;
                var sample = Math.Clamp(samples[i] + dither, -1.0, 1.0);
                var value = (short)Math.Round(sample * 32767.0, MidpointRounding.AwayFromZero);
                var offset = i * 2;
                bytes[offset] = (byte)(value & 0xFF);
                bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            }

            writer.Write(bytes, 0, read * 2);
            frames += read / format.Channels;
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
