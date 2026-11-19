using NAudio.Wave;
using System.Buffers.Binary;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>
/// Opens a source audio file as a float sample stream. WAV files use NAudio's native reader;
/// everything else (AAC/M4A/FLAC/MP3/WMA…) goes through Media Foundation.
/// </summary>
internal static class MediaDecoder
{
    /// <summary>KSDATAFORMAT_SUBTYPE_PCM（WAVE_FORMAT_EXTENSIBLE 的整数 PCM 子格式）。</summary>
    private static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT（WAVE_FORMAT_EXTENSIBLE 的 float32 子格式）。</summary>
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public static DecodedAudio Open(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new WaveFileReader(path);
            var waveFormat = reader.WaveFormat;
            var bitsPerSample = waveFormat.BitsPerSample;
            var duration = reader.TotalTime;

            // NAudio 2.2.1 的 WaveFileReader 把 WAVE_FORMAT_EXTENSIBLE 读成 WaveFormatExtraData，
            // 直接 ToSampleProvider() 会抛 "Unsupported source encoding"。这里按子格式还原成标准
            // WaveFormat 再交给 NAudio 的转换器（ADR-028 §3.6.1 要求多声道 WAV 也可分析/转换）。
            if (waveFormat.Encoding == WaveFormatEncoding.Extensible)
            {
                var standardFormat = TryCreateStandardWaveFormat(waveFormat);
                if (standardFormat is null)
                {
                    reader.Dispose();
                    throw new NotSupportedException($"不支持的 WAV 子格式：{waveFormat}");
                }

                return new DecodedAudio(
                    new BlockAlignedWaveProvider(reader, standardFormat).ToSampleProvider(),
                    reader,
                    bitsPerSample,
                    duration);
            }

            return new DecodedAudio(reader.ToSampleProvider(), reader, bitsPerSample, duration);
        }

        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        var mediaFoundationReader = new MediaFoundationReader(
            path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        return new DecodedAudio(
            mediaFoundationReader.ToSampleProvider(),
            mediaFoundationReader,
            0,
            mediaFoundationReader.TotalTime);
    }

    /// <summary>
    /// Reads the <c>dwChannelMask</c> of a WAVE_FORMAT_EXTENSIBLE format; returns null when the
    /// format carries no channel mask (plain PCM/float WAV, Media Foundation output).
    /// NAudio 2.2.1 exposes no public <c>ChannelMask</c> property, so the value is read from the
    /// serialized format header (dwChannelMask sits at byte offset 24 of WAVEFORMATEXTENSIBLE)
    /// or from <see cref="WaveFormatExtraData.ExtraData"/> for reader-produced formats.
    /// </summary>
    public static int? TryGetChannelMask(WaveFormat? waveFormat)
    {
        switch (waveFormat)
        {
            case WaveFormatExtensible extensible:
            {
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    extensible.Serialize(writer);
                }

                var bytes = stream.ToArray();
                return bytes.Length >= 28 ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)) : null;
            }

            case WaveFormatExtraData extras when extras.Encoding == WaveFormatEncoding.Extensible:
                var data = extras.ExtraData;
                return data is { Length: >= 6 } ? BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(2)) : null;

            default:
                return null;
        }
    }

    private static WaveFormat? TryCreateStandardWaveFormat(WaveFormat waveFormat)
    {
        var subFormat = TryGetSubFormat(waveFormat);
        var bits = waveFormat.BitsPerSample;
        if (subFormat == SubFormatIeeeFloat && bits == 32)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat(waveFormat.SampleRate, waveFormat.Channels);
        }

        if (subFormat == SubFormatPcm && bits is 8 or 16 or 24 or 32)
        {
            return new WaveFormat(waveFormat.SampleRate, bits, waveFormat.Channels);
        }

        return null;
    }

    private static Guid? TryGetSubFormat(WaveFormat waveFormat)
    {
        if (waveFormat is WaveFormatExtensible extensible)
        {
            return extensible.SubFormat;
        }

        if (waveFormat is WaveFormatExtraData extras && extras.ExtraData is { Length: >= 22 } data)
        {
            return new Guid(data.AsSpan(6, 16));
        }

        return null;
    }

    /// <summary>
    /// Presents a WAVE_FORMAT_EXTENSIBLE WAV as a standard PCM/float stream. The raw data layout is
    /// unchanged (same block align), only the format reported to NAudio's sample converters is
    /// normalized; reads are rounded down to complete frames because WaveFileReader rejects partial blocks.
    /// </summary>
    private sealed class BlockAlignedWaveProvider : IWaveProvider
    {
        private readonly WaveFileReader _reader;

        public BlockAlignedWaveProvider(WaveFileReader reader, WaveFormat waveFormat)
        {
            _reader = reader;
            WaveFormat = waveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public int Read(byte[] buffer, int offset, int count)
        {
            var complete = count - (count % WaveFormat.BlockAlign);
            return complete <= 0 ? 0 : _reader.Read(buffer, offset, complete);
        }
    }
}

/// <summary>A decoded stream plus the object that owns the underlying file handle.</summary>
/// <param name="Provider">交错的 float32 采样流。</param>
/// <param name="Owner">持有文件句柄、负责释放的解码器。</param>
/// <param name="SourceBitsPerSample">源文件位深；解码器不提供时（Media Foundation 路径）为 0。</param>
/// <param name="Duration">解码器报告的容器时长；无法获知时为 null。</param>
internal sealed record DecodedAudio(
    ISampleProvider Provider,
    IDisposable Owner,
    int SourceBitsPerSample,
    TimeSpan? Duration);
