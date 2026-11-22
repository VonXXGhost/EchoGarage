using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 以共享方式打开音频文件的采样读取器。与 <see cref="AudioFileReader"/> 不同，它允许其他进程/代码
/// 同时读取并删除文件（<see cref="FileShare.ReadWrite"/> 与 <see cref="FileShare.Delete"/>），
/// 这样播放期间标签写入器可以把新内容写到临时文件后原子替换原文件，而不必中断播放。
/// 支持 wav / aiff 原生解码，其余格式（mp3、m4a/aac、flac、wma 等）走 Media Foundation。
/// </summary>
public sealed class SharedAudioFileReader : ISampleProvider, IDisposable
{
    private readonly FileStream _stream;
    private readonly WaveStream _reader;
    private readonly SampleChannel _channel;
    private bool _disposed;

    public SharedAudioFileReader(string path)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        _reader = extension switch
        {
            ".wav" => new WaveFileReader(_stream),
            ".aiff" or ".aif" => new AiffFileReader(_stream),
            _ => new StreamMediaFoundationReader(_stream, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = false,
                RepositionInRead = true,
            }),
        };
        DecoderName = extension switch
        {
            ".wav" => "WAV 原生解码（WaveFileReader）",
            ".aiff" or ".aif" => "AIFF 原生解码（AiffFileReader）",
            _ => "Media Foundation 解码器"
        };
        _channel = new SampleChannel(_reader, true);
    }

    /// <summary>解码器名字（用于「音频信息」窗口展示）。</summary>
    public string DecoderName { get; }

    public WaveFormat WaveFormat => _channel.WaveFormat;

    public TimeSpan TotalTime => _reader.TotalTime;

    public TimeSpan CurrentTime
    {
        get => _reader.CurrentTime;
        set => _reader.CurrentTime = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _channel.Read(buffer, offset, count);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _reader.Dispose();
        }
        catch
        {
            // Ignore dispose errors.
        }

        try
        {
            _stream.Dispose();
        }
        catch
        {
            // Ignore dispose errors.
        }
    }
}
