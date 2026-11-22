using EchoGarage.Core.Models;
using NAudio.CoreAudioApi;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 枚举输出设备（WASAPI Render / Active）的共享实现。播放器与设备监视器都通过它读取
/// 同一份快照，避免两处逻辑对“默认设备”的判定不一致。
/// </summary>
internal static class WasapiOutputDeviceEnumerator
{
    /// <summary>
    /// 返回当前启用（Active）的输出设备；顺序沿用系统枚举顺序。异常由调用方负责记录日志。
    /// </summary>
    public static List<AudioDeviceInfo> GetActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return GetActiveRenderDevices(enumerator);
    }

    /// <summary>复用调用方已有的 <see cref="MMDeviceEnumerator"/>（监视器线程上持有）。</summary>
    public static List<AudioDeviceInfo> GetActiveRenderDevices(MMDeviceEnumerator enumerator)
    {
        var defaultId = TryGetDefaultDeviceId(enumerator);
        var list = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                list.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
            finally
            {
                device.Dispose();
            }
        }

        return list;
    }

    /// <summary>指定端点 ID 是否仍处于启用状态（设备被拔出 / 禁用后返回 false）。</summary>
    public static bool IsDeviceActive(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try
                {
                    if (string.Equals(device.ID, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // 音频服务不可用 / 设备刚被移除时枚举可能失败，按“不可用”处理即可。
        }

        return false;
    }

    private static string? TryGetDefaultDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return defaultDevice.ID;
        }
        catch
        {
            // 没有任何输出设备时 GetDefaultAudioEndpoint 会失败；其余设备仍可列出。
            return null;
        }
    }
}
