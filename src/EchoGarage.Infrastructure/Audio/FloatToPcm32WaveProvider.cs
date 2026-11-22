using NAudio.Wave;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 把 32-bit float 采样转换成 32-bit 整数 PCM 容器（每个样本 4 字节），并支持指定有效位深
/// （wValidBitsPerSample），例如 24-in-32：值左对齐到 32 位容器的高 24 位。
/// 用于 WASAPI 独占模式中设备只接受 WAVEFORMATEXTENSIBLE 24-in-32 的场景，避免被逼退化成 16-bit。
/// </summary>
internal sealed class FloatToPcm32WaveProvider : IWaveProvider
{
    private const int BytesPerSample = 4;

    private readonly ISampleProvider _source;
    private readonly int _validBits;
    private readonly int _shift;
    private readonly double _scale;
    private readonly float[] _samples = new float[2048];
    private int _sampleCount;
    private int _sampleIndex;

    public FloatToPcm32WaveProvider(ISampleProvider source, WaveFormat waveFormat, int validBits)
    {
        _source = source;
        WaveFormat = waveFormat;
        _validBits = Math.Clamp(validBits, 8, 32);
        _shift = 32 - _validBits;
        _scale = (1L << (_validBits - 1)) - 1;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        var requestedSamples = count / BytesPerSample;
        var written = 0;
        while (written < requestedSamples)
        {
            if (_sampleIndex >= _sampleCount)
            {
                var want = Math.Min(_samples.Length, requestedSamples - written);
                _sampleCount = _source.Read(_samples, 0, want);
                _sampleIndex = 0;
                if (_sampleCount <= 0)
                {
                    break;
                }
            }

            var value = ToPcm32(_samples[_sampleIndex++]);
            var position = offset + written * BytesPerSample;
            buffer[position] = (byte)value;
            buffer[position + 1] = (byte)(value >> 8);
            buffer[position + 2] = (byte)(value >> 16);
            buffer[position + 3] = (byte)(value >> 24);
            written++;
        }

        return written * BytesPerSample;
    }

    private int ToPcm32(float sample)
    {
        var clamped = Math.Clamp(sample, -1f, 1f);
        var value = (int)Math.Round(clamped * _scale);
        return value << _shift;
    }
}
