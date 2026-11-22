using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 比较两份输出设备快照，得到新增 / 移除 / 重命名 / 默认设备变化。
/// 纯逻辑（不访问 WASAPI），供设备监视器与界面播报共用，便于单元测试。
/// </summary>
public static class AudioDeviceChangeDetector
{
    public static AudioDeviceChangeSet Compare(
        IReadOnlyList<AudioDeviceInfo>? previous,
        IReadOnlyList<AudioDeviceInfo>? current)
    {
        var oldList = previous ?? [];
        var newList = current ?? [];
        if (oldList.Count == 0 && newList.Count == 0)
        {
            return AudioDeviceChangeSet.Empty;
        }

        var oldById = new Dictionary<string, AudioDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in oldList)
        {
            oldById[device.Id] = device;
        }

        var newById = new Dictionary<string, AudioDeviceInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in newList)
        {
            newById[device.Id] = device;
        }

        var added = newList.Where(d => !oldById.ContainsKey(d.Id)).ToList();
        var removed = oldList.Where(d => !newById.ContainsKey(d.Id)).ToList();
        var renamed = newList
            .Where(d => oldById.TryGetValue(d.Id, out var old) &&
                        !string.Equals(old.FriendlyName, d.FriendlyName, StringComparison.Ordinal))
            .ToList();

        return new AudioDeviceChangeSet(
            added,
            removed,
            renamed,
            oldList.FirstOrDefault(d => d.IsDefault),
            newList.FirstOrDefault(d => d.IsDefault));
    }
}
