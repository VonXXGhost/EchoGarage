namespace EchoGarage.Core.Abstractions;

/// <summary>单个文件的复制 / 移动 / 删除结果；<see cref="Error"/> 为 null 表示成功。</summary>
public sealed record FileOperationItem(string SourcePath, string? TargetPath, string? Error)
{
    public bool Succeeded => Error is null;

    public static FileOperationItem Success(string sourcePath, string targetPath) =>
        new(sourcePath, targetPath, null);

    public static FileOperationItem Failure(string sourcePath, string error) =>
        new(sourcePath, null, error);
}

/// <summary>一批文件操作的结果；成功 / 失败按下标一一对应来源文件。</summary>
public sealed class FileOperationResult
{
    public FileOperationResult(IReadOnlyList<FileOperationItem> items) => Items = items;

    public IReadOnlyList<FileOperationItem> Items { get; }

    public int Succeeded => Items.Count(item => item.Succeeded);

    public int Failed => Items.Count - Succeeded;

    /// <summary>失败条目的错误信息（用于状态栏 / 日志汇总）。</summary>
    public IReadOnlyList<string> Errors =>
        Items.Where(item => !item.Succeeded)
            .Select(item => $"{Path.GetFileName(item.SourcePath)}：{item.Error}")
            .ToList();
}

/// <summary>回收站抽象（ADR-021）：真实实现用 Shell 的 SHFileOperation，测试里注入假实现。</summary>
public interface IRecycleBin
{
    /// <summary>把文件移到回收站；文件不存在或系统拒绝时返回 false（绝不永久删除）。</summary>
    bool TryDelete(string path);
}

/// <summary>
/// 文件操作服务（ADR-021）：复制 / 移动 / 删除到回收站。只做文件系统动作，
/// 不碰数据库；媒体库同步由 App 层在操作成功后调用仓储完成。
/// </summary>
public interface IFileOperationService
{
    /// <summary>复制到目录；同名文件自动追加 “ (2)” 等序号，不覆盖；目录不存在会创建。</summary>
    Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourceFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>移动到目录；跨卷时退化为复制 + 删除；同名文件同样自动避让。</summary>
    Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourceFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>删除到回收站；任何失败都保留原文件。</summary>
    Task<FileOperationResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default);
}
