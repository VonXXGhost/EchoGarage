using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 输出音量记忆与音量上限的纯逻辑（ADR-033）。
/// <para>
/// 音量滑块显示值（0–1）按模式映射到该模式的上限：实际增益 = 显示音量 × 上限。
/// 每个输出设备分别记住共享 / 独占模式的显示音量；没有记忆时回落到全局
/// <see cref="AppSettings.Volume"/>。设备键使用当前实际输出的端点 ID，
/// 因此“跟随系统默认设备”时不同默认设备也各自有音量记忆。
/// </para>
/// </summary>
public static class OutputVolumeResolver
{
    /// <summary>音量上限下限：5%。避免上限被拖到 0 后完全无声、无法再调回来。</summary>
    public const double MinCeiling = 0.05;

    /// <summary>UI 音量步进（百分比）：音量条滚轮与音量快捷键共用。</summary>
    public const double VolumeStepPercent = 5;

    /// <summary>最多记住的设备数，防止长期插拔累积无限条目。</summary>
    public const int MaxRememberedDevices = 32;

    /// <summary>
    /// 按 UI 步进调整显示音量（0–100 百分比）：先加上 / 减去 <see cref="VolumeStepPercent"/>，
    /// 再四舍五入到最近的 5% 刻度（AwayFromZero），最后夹到 0–100。音量条滚轮与音量快捷键
    /// 共用同一公式，保证两者行为完全一致。<paramref name="direction"/> 为正向上、为负向下，
    /// 0 表示只把当前值对齐到刻度。
    /// </summary>
    public static double StepDisplayPercent(double currentPercent, int direction)
    {
        var current = double.IsFinite(currentPercent) ? currentPercent : 0;
        var step = Math.Sign(direction) * VolumeStepPercent;
        var target = Math.Round((current + step) / VolumeStepPercent, MidpointRounding.AwayFromZero) *
                     VolumeStepPercent;
        return Math.Clamp(target, 0, 100);
    }

    /// <summary>把任意上限值规范到 [<see cref="MinCeiling"/>, 1]；非有限值回退 100%。</summary>
    public static double ClampCeiling(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinCeiling, 1.0) : 1.0;

    /// <summary>取指定模式的音量上限（0–1）。</summary>
    public static double GetCeiling(AppSettings settings, OutputMode mode)
    {
        var ceilings = settings.VolumeCeilings ??= new OutputVolumeCeilings();
        return ClampCeiling(mode == OutputMode.Exclusive ? ceilings.Exclusive : ceilings.Shared);
    }

    /// <summary>
    /// 取指定设备在指定模式下应显示的音量（0–1）：优先设备记忆，缺失回落到全局音量。
    /// </summary>
    public static double ResolveDisplayVolume(AppSettings settings, string? deviceId, OutputMode mode)
    {
        var memory = FindMemory(settings.DeviceVolumes, deviceId);
        if (memory is not null)
        {
            var remembered = mode == OutputMode.Exclusive ? memory.Exclusive : memory.Shared;
            if (remembered is { } value && double.IsFinite(value))
            {
                return Math.Clamp(value, 0, 1);
            }
        }

        return Math.Clamp(double.IsFinite(settings.Volume) ? settings.Volume : 0, 0, 1);
    }

    /// <summary>实际输出增益 = 显示音量 × 上限，两者都规范到 0–1。</summary>
    public static double GetEffectiveGain(double displayVolume, double ceiling) =>
        Math.Clamp(double.IsFinite(displayVolume) ? displayVolume : 0, 0, 1) * ClampCeiling(ceiling);

    /// <summary>
    /// 记住用户对“当前设备 + 当前模式”设置的显示音量，并同步更新全局回落音量。
    /// 设备为空（未检测到输出设备）时只更新全局回落音量。
    /// </summary>
    public static void Remember(AppSettings settings, string? deviceId, OutputMode mode, double displayVolume)
    {
        var value = Math.Clamp(double.IsFinite(displayVolume) ? displayVolume : 0, 0, 1);
        settings.Volume = value;

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var key = deviceId.Trim();
        var volumes = settings.DeviceVolumes ??=
            new Dictionary<string, OutputDeviceVolume>(StringComparer.OrdinalIgnoreCase);
        var existingKey = volumes.Keys.FirstOrDefault(
            existing => string.Equals(existing, key, StringComparison.OrdinalIgnoreCase));

        if (existingKey is null)
        {
            if (volumes.Count >= MaxRememberedDevices)
            {
                // 已达上限：只保留已有设备，避免配置无限增长。
                return;
            }

            existingKey = key;
            volumes[existingKey] = new OutputDeviceVolume();
        }

        var memory = volumes[existingKey] ??= new OutputDeviceVolume();
        if (mode == OutputMode.Exclusive)
        {
            memory.Exclusive = value;
        }
        else
        {
            memory.Shared = value;
        }
    }

    /// <summary>
    /// 规范化设备音量记忆：去掉空白 / 无效条目，音量夹到 0–1，
    /// 设备 ID 去重（忽略大小写），并限制条目总数。见 ADR-033。
    /// </summary>
    public static Dictionary<string, OutputDeviceVolume> NormalizeMemories(
        Dictionary<string, OutputDeviceVolume>? source,
        IAppLogger? logger = null)
    {
        var result = new Dictionary<string, OutputDeviceVolume>(StringComparer.OrdinalIgnoreCase);
        if (source is null || source.Count == 0)
        {
            return result;
        }

        var dropped = 0;
        foreach (var (rawKey, rawValue) in source)
        {
            var key = rawKey?.Trim();
            if (string.IsNullOrEmpty(key) || rawValue is null)
            {
                dropped++;
                continue;
            }

            var shared = NormalizeValue(rawValue.Shared);
            var exclusive = NormalizeValue(rawValue.Exclusive);
            if (shared is null && exclusive is null)
            {
                dropped++;
                continue;
            }

            if (result.ContainsKey(key))
            {
                dropped++;
            }

            result[key] = new OutputDeviceVolume { Shared = shared, Exclusive = exclusive };
        }

        if (result.Count > MaxRememberedDevices)
        {
            var extraKeys = result.Keys.Skip(MaxRememberedDevices).ToList();
            foreach (var extraKey in extraKeys)
            {
                result.Remove(extraKey);
            }

            logger?.Warn(
                $"Output volume memory limited to {MaxRememberedDevices} devices; " +
                $"dropped {extraKeys.Count} old entr{(extraKeys.Count == 1 ? "y" : "ies")}.");
        }

        if (dropped > 0)
        {
            logger?.Warn($"Dropped {dropped} invalid output volume memor{(dropped == 1 ? "y" : "ies")}.");
        }

        return result;
    }

    private static double? NormalizeValue(double? value) =>
        value is { } finite && double.IsFinite(finite) ? Math.Clamp(finite, 0, 1) : null;

    private static OutputDeviceVolume? FindMemory(
        Dictionary<string, OutputDeviceVolume>? volumes,
        string? deviceId)
    {
        if (volumes is null || volumes.Count == 0 || string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var key = deviceId.Trim();
        if (volumes.TryGetValue(key, out var exact))
        {
            return exact;
        }

        foreach (var pair in volumes)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
