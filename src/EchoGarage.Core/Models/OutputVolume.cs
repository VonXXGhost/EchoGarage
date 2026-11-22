namespace EchoGarage.Core.Models;

/// <summary>
/// 单个输出设备按模式记住的音量（UI 显示值 0–1）；<c>null</c> 表示该模式尚无记忆，
/// 回落到 <see cref="AppSettings.Volume"/>。见 ADR-033。
/// </summary>
public sealed class OutputDeviceVolume
{
    public double? Shared { get; set; }
    public double? Exclusive { get; set; }
}

/// <summary>
/// 按模式配置的音量上限（0–1）：音量滑块的 100% 即映射到该上限，
/// 用于在独占模式（绕过系统混音器）下限制最大输出功率。见 ADR-033。
/// </summary>
public sealed class OutputVolumeCeilings
{
    public double Shared { get; set; } = 1.0;
    public double Exclusive { get; set; } = 1.0;
}
