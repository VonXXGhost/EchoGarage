using System.Runtime.InteropServices;
using EchoGarage.Infrastructure.Audio;
using NAudio.Wave;

namespace EchoGarage.Tests;

/// <summary>
/// 独占模式 24-in-32 相关的格式构造 / 有效位读取 / float→PCM32 转换。
/// 这些是不依赖真实声卡的纯逻辑，用来锁住位深协商里最容易出错的位移与字节布局。
/// </summary>
public sealed class ExclusivePcmFormatTests
{
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    [Fact]
    public void CreatePcm_24In32_StoresValidBitsInExtensibleBlock()
    {
        var format = ExtensibleWaveFormatFactory.CreatePcm(96000, 2, 32, 24);

        var extensible = Assert.IsType<WaveFormatExtensible>(format);
        Assert.Equal(WaveFormatEncoding.Extensible, format.Encoding);
        Assert.Equal(96000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(22, format.ExtraSize);
        Assert.Equal(PcmSubFormat, extensible.SubFormat);
        Assert.Equal(24, ExtensibleWaveFormatFactory.GetValidBits(format));

        // NAudio 2.2.1 的 WaveFormatExtensible 没有公开有效位 / 声道掩码属性，直接核对原始布局。
        var pointer = WaveFormat.MarshalToPtr(format);
        try
        {
            Assert.Equal(24, Marshal.ReadInt16(pointer, 18));
            Assert.Equal(0x3, Marshal.ReadInt32(pointer, 20));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Fact]
    public void GetValidBits_FallsBackToContainerBitsForNonExtensibleFormats()
    {
        Assert.Equal(16, ExtensibleWaveFormatFactory.GetValidBits(new WaveFormat(48000, 16, 2)));
        Assert.Equal(32, ExtensibleWaveFormatFactory.GetValidBits(
            WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)));
    }

    [Fact]
    public void FloatToPcm32_ValidBits24_LeftAlignsSamplesIn32BitContainer()
    {
        var source = new ArraySampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(96000, 2),
            [0f, 0f, 0.5f, -0.5f, 1f, -1f]);
        var format = ExtensibleWaveFormatFactory.CreatePcm(96000, 2, 32, 24);
        var provider = new FloatToPcm32WaveProvider(source, format, 24);
        var buffer = new byte[16];

        var read = provider.Read(buffer, 0, buffer.Length);

        Assert.Equal(16, read);
        Assert.Equal(0, BitConverter.ToInt32(buffer, 0));
        Assert.Equal(0, BitConverter.ToInt32(buffer, 4));
        // 0.5 * ((1 << 23) - 1) = 4194303.5 → 4194304，再左移 8 位填满 32-bit 容器。
        Assert.Equal(0x40000000, BitConverter.ToInt32(buffer, 8));
        Assert.Equal(unchecked((int)0xC0000000), BitConverter.ToInt32(buffer, 12));
    }

    [Fact]
    public void FloatToPcm32_ClampsAndUsesRemainingBuffer()
    {
        var source = new ArraySampleProvider(
            WaveFormat.CreateIeeeFloatWaveFormat(96000, 2),
            [2f, -2f, 0f]);
        var format = ExtensibleWaveFormatFactory.CreatePcm(96000, 2, 32, 24);
        var provider = new FloatToPcm32WaveProvider(source, format, 24);
        var buffer = new byte[8];

        Assert.Equal(8, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(0x7FFFFF00, BitConverter.ToInt32(buffer, 0));
        Assert.Equal(unchecked((int)0x80000100), BitConverter.ToInt32(buffer, 4));
        // 第三个样本还在内部缓冲里：下一次 Read 先把它送出去，再返回 0。
        Assert.Equal(4, provider.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, BitConverter.ToInt32(buffer, 0));
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));
    }

    private sealed class ArraySampleProvider(WaveFormat waveFormat, float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
