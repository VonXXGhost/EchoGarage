using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 一次输出设备选择的结果。
/// <para>
/// <see cref="PreferredDeviceId"/> 为用户上次使用的设备（null / 空白 = 跟随系统默认）。
/// 当偏好设备存在时直接使用它；不存在时回退到系统默认设备，并标记 <see cref="IsFallback"/>，
/// 让界面能提示“上次使用的设备不可用”。偏好设备重新出现后调用方应自动切回。
/// </para>
/// </summary>
public sealed record OutputDeviceSelection(
    AudioDeviceInfo? Device,
    string? PreferredDeviceId,
    bool IsPreferredAvailable)
{
    public bool HasPreference => !string.IsNullOrWhiteSpace(PreferredDeviceId);

    /// <summary>配置过具体的偏好设备，但它当前不存在，因此使用了系统默认设备。</summary>
    public bool IsFallback => HasPreference && !IsPreferredAvailable && Device is not null;

    /// <summary>没有偏好设备，主动跟随系统默认设备。</summary>
    public bool IsFollowingDefault => !HasPreference;
}

/// <summary>
/// 纯逻辑的“首选输出设备”选择规则，与 WASAPI 解耦以便单元测试。
/// 选择顺序：偏好设备 → 系统默认设备 → 列表第一个设备 → 无设备。
/// </summary>
public static class OutputDeviceSelector
{
    public static OutputDeviceSelection Select(
        IReadOnlyList<AudioDeviceInfo>? devices,
        string? preferredDeviceId)
    {
        var list = devices ?? [];
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            var preferred = list.FirstOrDefault(
                d => string.Equals(d.Id, preferredDeviceId, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
            {
                return new OutputDeviceSelection(preferred, preferredDeviceId, true);
            }

            return new OutputDeviceSelection(DefaultOrFirst(list), preferredDeviceId, false);
        }

        return new OutputDeviceSelection(DefaultOrFirst(list), null, false);
    }

    private static AudioDeviceInfo? DefaultOrFirst(IReadOnlyList<AudioDeviceInfo> devices) =>
        devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
}
