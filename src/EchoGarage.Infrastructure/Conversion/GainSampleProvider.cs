using NAudio.Wave;

namespace EchoGarage.Infrastructure.Conversion;

/// <summary>
/// ADR-028 §3.6 的线性增益采样源：包装解码后的 <see cref="ISampleProvider"/>，读取时逐采样乘以
/// <c>10^(GainDb/20)</c>，波形格式（采样率/声道/编码）原样透传。
/// 转换器只在 <see cref="EchoGarage.Core.Models.ConversionRequest.GainDb"/> 非 0 时才包装；
/// 峰值安全（真峰值天花板）由调用方先用 <see cref="EchoGarage.Core.Services.LoudnessGain"/> 计算，
/// 这里不做任何限幅或归一化——超出 [-1,1] 的采样交给下游写 WAV / 编码器统一 clamp。
/// </summary>
public sealed class GainSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _factor;

    /// <param name="source">解码后的交错 float32 采样源。</param>
    /// <param name="gainDb">线性增益（dB）；0 表示原样透传。</param>
    public GainSampleProvider(ISampleProvider source, double gainDb)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (!double.IsFinite(gainDb))
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "增益必须是有限数值（dB）。");
        }

        GainDb = gainDb;
        _factor = (float)Math.Pow(10.0, gainDb / 20.0);
    }

    /// <summary>施加的线性增益（dB）。</summary>
    public double GainDb { get; }

    /// <inheritdoc />
    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <inheritdoc />
    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0 || _factor == 1f)
        {
            return read;
        }

        for (var index = 0; index < read; index++)
        {
            buffer[offset + index] *= _factor;
        }

        return read;
    }
}
