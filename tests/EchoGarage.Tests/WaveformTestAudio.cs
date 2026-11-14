using System.Collections.Concurrent;
using System.Text;
using EchoGarage.Core.Abstractions;

namespace EchoGarage.Tests;

/// <summary>
/// 测试用 WAV 生成器：手写 44 字节 RIFF 头 + 单声道 16-bit PCM，不提交任何二进制素材。
/// </summary>
internal static class WaveformTestAudio
{
    public const int DefaultSampleRate = 44100;

    /// <summary>写一段单声道 16-bit PCM WAV，<paramref name="sampleAt"/> 返回 [-1, 1] 的采样值。</summary>
    public static string Write(string path, double seconds, Func<int, double> sampleAt, int sampleRate = DefaultSampleRate)
    {
        var sampleCount = (int)Math.Round(seconds * sampleRate);
        var dataBytes = sampleCount * 2;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);              // PCM
        writer.Write((short)1);              // 单声道
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);        // byte rate
        writer.Write((short)2);              // block align
        writer.Write((short)16);             // bits per sample
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var i = 0; i < sampleCount; i++)
        {
            var value = Math.Clamp(sampleAt(i), -1.0, 1.0);
            writer.Write((short)Math.Round(value * short.MaxValue, MidpointRounding.AwayFromZero));
        }

        return path;
    }

    /// <summary>正弦波（默认振幅 0.5，约 −6 dBFS）。</summary>
    public static string WriteSine(
        string path,
        double seconds,
        double frequency,
        double amplitude = 0.5,
        int sampleRate = DefaultSampleRate) =>
        Write(
            path,
            seconds,
            i => amplitude * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate),
            sampleRate);

    /// <summary>数字静音。</summary>
    public static string WriteSilence(string path, double seconds, int sampleRate = DefaultSampleRate) =>
        Write(path, seconds, _ => 0.0, sampleRate);

    /// <summary>
    /// 振幅缓慢起伏的正弦：用于分块一致性 / 渐进发布测试，避免“恒定信号经分位拉伸后高度全 0”。
    /// </summary>
    public static string WriteShapedSine(
        string path,
        double seconds,
        double frequency = 440.0,
        int sampleRate = DefaultSampleRate)
    {
        var sampleCount = (int)Math.Round(seconds * sampleRate);
        var fadeSamples = Math.Max(1, (int)(0.03 * sampleRate));
        return Write(
            path,
            seconds,
            i =>
            {
                var position = (double)i / sampleCount;
                var amplitude = 0.12 + 0.5 * (0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 0.8 * position));
                var fade = Math.Min(1.0, Math.Min((double)i / fadeSamples, (double)(sampleCount - i) / fadeSamples));
                return amplitude * fade * Math.Sin(2.0 * Math.PI * frequency * i / sampleRate);
            },
            sampleRate);
    }
}

/// <summary>同步收集 IProgress 回调（<see cref="Progress{T}"/> 会异步 post，测试里不可靠）。</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly List<T> _items = [];

    public Action<T>? OnReport { get; set; }

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_items)
            {
                return _items.ToArray();
            }
        }
    }

    public void Report(T value)
    {
        lock (_items)
        {
            _items.Add(value);
        }

        OnReport?.Invoke(value);
    }
}

/// <summary>内存日志：断言服务层是否按级别写出了预期内容。</summary>
internal sealed class RecordingLogger : IAppLogger
{
    private readonly ConcurrentQueue<string> _infos = new();
    private readonly ConcurrentQueue<string> _warnings = new();
    private readonly ConcurrentQueue<string> _errors = new();
    private readonly ConcurrentQueue<string> _debugs = new();

    public string LogDirectory => Path.GetTempPath();

    public IReadOnlyCollection<string> Infos => _infos;

    public IReadOnlyCollection<string> Warnings => _warnings;

    public IReadOnlyCollection<string> Errors => _errors;

    public IReadOnlyCollection<string> Debugs => _debugs;

    public void Info(string message) => _infos.Enqueue(message);

    public void Warn(string message) => _warnings.Enqueue(message);

    public void Error(string message, Exception? exception = null) =>
        _errors.Enqueue(exception is null ? message : $"{message} | {exception.Message}");

    public void Debug(string message) => _debugs.Enqueue(message);
}
