using System.Runtime.InteropServices;
using NAudio.Wave;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 构造 WAVEFORMATEXTENSIBLE 格式。
/// NAudio 2.2.1 的 <see cref="WaveFormatExtensible"/> 公开构造函数无法表达「32-bit 容器 +
/// 24-bit 有效位」这类格式（wValidBitsPerSample=24），只能按 WAVEFORMATEXTENSIBLE 的内存布局
/// 写原始字节再反序列化。很多 DAC 在独占模式下只接受这种 24-in-32 格式；没有它就只能退化成
/// 16-bit 输出，这是实际会被听到的位深损失。
/// </summary>
internal static class ExtensibleWaveFormatFactory
{
    private const int WaveFormatExtensibleSize = 40;
    private const short ExtensibleFormatTag = -2; // WAVE_FORMAT_EXTENSIBLE = 0xFFFE

    /// <summary>KSDATAFORMAT_SUBTYPE_PCM 的 GUID 字节（小端序）。</summary>
    private static readonly byte[] PcmSubFormat =
    [
        0x01, 0x00, 0x00, 0x00,
        0x00, 0x00,
        0x10, 0x00,
        0x80, 0x00,
        0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
    ];

    /// <summary>
    /// 创建整数 PCM 的 WAVEFORMATEXTENSIBLE。<paramref name="containerBits"/> 是每个样本占用的
    /// 位数（通常 32），<paramref name="validBits"/> 是其中真正有效的位数（例如 24）。
    /// </summary>
    public static WaveFormat CreatePcm(int sampleRate, int channels, int containerBits, int validBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(containerBits, 8);
        validBits = Math.Clamp(validBits, 8, containerBits);

        var blockAlign = channels * containerBits / 8;
        var pointer = Marshal.AllocHGlobal(WaveFormatExtensibleSize);
        try
        {
            Marshal.WriteInt16(pointer, 0, ExtensibleFormatTag);
            Marshal.WriteInt16(pointer, 2, (short)channels);
            Marshal.WriteInt32(pointer, 4, sampleRate);
            Marshal.WriteInt32(pointer, 8, sampleRate * blockAlign);
            Marshal.WriteInt16(pointer, 12, (short)blockAlign);
            Marshal.WriteInt16(pointer, 14, (short)containerBits);
            Marshal.WriteInt16(pointer, 16, 22); // cbSize
            Marshal.WriteInt16(pointer, 18, (short)validBits);
            Marshal.WriteInt32(pointer, 20, GetChannelMask(channels));
            Marshal.Copy(PcmSubFormat, 0, pointer + 24, PcmSubFormat.Length);
            return WaveFormat.MarshalFromPtr(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>读取格式的有效位深；不是 WAVEFORMATEXTENSIBLE 时返回容器位深。</summary>
    public static int GetValidBits(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible || format.ExtraSize < 22)
        {
            return format.BitsPerSample;
        }

        var pointer = WaveFormat.MarshalToPtr(format);
        try
        {
            var validBits = Marshal.ReadInt16(pointer, 18);
            return validBits is > 0 and <= 32 ? validBits : format.BitsPerSample;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>WAVEFORMATEXTENSIBLE 的 dwChannelMask（只覆盖播放器实际会用到的 1–2 声道）。</summary>
    private static int GetChannelMask(int channels) => channels switch
    {
        1 => 0x4,  // SPEAKER_FRONT_CENTER
        2 => 0x3,  // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
        _ => 0
    };
}
