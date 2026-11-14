namespace EchoGarage.Core.Services;

/// <summary>
/// 复制 / 移动目标文件名的纯函数规则（ADR-021）：目标目录已有同名文件时追加 “ (2)”“ (3)”…
/// 依次避让，绝不覆盖。文件是否存在由调用方通过 <paramref name="fileExists"/> 提供，
/// 这样规则可以脱离文件系统单独测试。
/// </summary>
public static class FileOperationPlanner
{
    /// <summary>同名文件的最大避让次数；超过视为异常，抛 <see cref="IOException"/>。</summary>
    public const int MaxAttempts = 999;

    /// <summary>目标文件是否会和源文件落在同一目录（同一目录的复制 / 移动没有意义）。</summary>
    public static bool IsSameDirectory(string? directory, string? sourceFilePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(sourceFilePath))
            {
                return false;
            }

            var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceFilePath));
            if (string.IsNullOrEmpty(sourceDirectory))
            {
                return false;
            }

            var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            sourceDirectory = Path.TrimEndingDirectorySeparator(sourceDirectory);
            return string.Equals(destination, sourceDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算目标路径：<c>&lt;directory&gt;\&lt;原文件名&gt;</c>；若已存在则尝试
    /// <c>&lt;原名&gt; (2).&lt;扩展名&gt;</c>、<c> (3)</c> …
    /// </summary>
    public static string ResolveTargetPath(
        string destinationDirectory,
        string sourceFilePath,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(fileExists);

        var fileName = Path.GetFileName(sourceFilePath);
        if (string.IsNullOrEmpty(fileName))
        {
            throw new IOException($"无法从路径取得文件名：{sourceFilePath}");
        }

        var target = Path.Combine(destinationDirectory, fileName);
        if (!fileExists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var attempt = 2; attempt <= MaxAttempts; attempt++)
        {
            target = Path.Combine(destinationDirectory, $"{stem} ({attempt}){extension}");
            if (!fileExists(target))
            {
                return target;
            }
        }

        throw new IOException($"目标目录里同名文件过多，无法为 {fileName} 生成新文件名。");
    }
}
