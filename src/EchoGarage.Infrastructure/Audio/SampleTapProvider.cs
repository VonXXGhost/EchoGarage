using EchoGarage.Core.Abstractions;
using NAudio.Wave;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 把播放链上的样本顺手交给可视化抽头（ADR-023 §6.2）：只转发 <see cref="Read"/>，不做缓冲、
/// 不分配、不阻塞，因此可以直接挂在 WASAPI 的音频线程回调链上。
/// </summary>
internal sealed class SampleTapProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly IAudioSampleSink _sink;

    public SampleTapProvider(ISampleProvider source, IAudioSampleSink sink)
    {
        _source = source;
        _sink = sink;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read > 0)
        {
            _sink.Write(buffer, offset, read);
        }

        return read;
    }
}
