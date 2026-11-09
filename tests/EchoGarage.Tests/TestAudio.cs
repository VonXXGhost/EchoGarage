using System.Text;

namespace EchoGarage.Tests;

internal static class TestAudio
{
    // 1x1 transparent PNG.
    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public static string CreateWav(string path, int seconds = 2, int sampleRate = 44100, short frequency = 440)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);

        var sampleCount = sampleRate * seconds;
        var dataSize = sampleCount * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);

        for (var i = 0; i < sampleCount; i++)
        {
            var sample = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 8000);
            writer.Write(sample);
        }

        writer.Flush();
        return path;
    }

    public static byte[] CoverBytes() => PngBytes;

    public static void Touch(string path)
    {
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
    }
}

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(AppContext.BaseDirectory, "testwork", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public string CreateFolder(string name)
    {
        var path = Combine(name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
