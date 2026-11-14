using NAudio.Wave;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>
/// Opens a source audio file as a float sample stream. WAV files use NAudio's native reader;
/// everything else (AAC/M4A/FLAC/MP3/WMA…) goes through Media Foundation.
/// </summary>
internal static class MediaDecoder
{
    public static DecodedAudio Open(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var reader = new WaveFileReader(path);
            var bitsPerSample = reader.WaveFormat.BitsPerSample;
            return new DecodedAudio(reader.ToSampleProvider(), reader, bitsPerSample);
        }

        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        var mediaFoundationReader = new MediaFoundationReader(
            path,
            new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        return new DecodedAudio(mediaFoundationReader.ToSampleProvider(), mediaFoundationReader, 0);
    }
}

/// <summary>A decoded stream plus the object that owns the underlying file handle.</summary>
internal sealed record DecodedAudio(ISampleProvider Provider, IDisposable Owner, int SourceBitsPerSample);
