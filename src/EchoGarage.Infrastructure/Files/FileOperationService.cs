using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Windows;

namespace EchoGarage.Infrastructure.Files;

/// <summary>
/// 复制 / 移动 / 删除到回收站（ADR-021）。文件 IO 放在线程池执行，UI 线程只处理结果；
/// 目标目录不存在时自动创建，同名文件追加 “ (2)” 等序号，绝不覆盖。
/// </summary>
public sealed class FileOperationService : IFileOperationService
{
    private readonly IAppLogger _logger;
    private readonly IRecycleBin _recycleBin;

    public FileOperationService(IAppLogger logger, IRecycleBin? recycleBin = null)
    {
        _logger = logger;
        _recycleBin = recycleBin ?? new ShellRecycleBin(logger);
    }

    public Task<FileOperationResult> CopyAsync(
        IReadOnlyList<string> sourceFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Transfer(sourceFiles, destinationDirectory, move: false, cancellationToken), cancellationToken);

    public Task<FileOperationResult> MoveAsync(
        IReadOnlyList<string> sourceFiles,
        string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Transfer(sourceFiles, destinationDirectory, move: true, cancellationToken), cancellationToken);

    public Task<FileOperationResult> DeleteToRecycleBinAsync(
        IReadOnlyList<string> sourceFiles,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => DeleteToRecycleBin(sourceFiles, cancellationToken), cancellationToken);

    private FileOperationResult Transfer(
        IReadOnlyList<string> sourceFiles,
        string destinationDirectory,
        bool move,
        CancellationToken cancellationToken)
    {
        var items = new List<FileOperationItem>(sourceFiles.Count);
        string? destination;
        try
        {
            destination = PathUtil.Normalize(destinationDirectory);
        }
        catch (Exception ex)
        {
            _logger.Warn($"文件操作目标目录无效 path={destinationDirectory} error={ex.Message}");
            return new FileOperationResult(sourceFiles
                .Select(path => FileOperationItem.Failure(path, "目标目录无效"))
                .ToList());
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            _logger.Warn($"创建目标目录失败 path={destination} error={ex.Message}");
            return new FileOperationResult(sourceFiles
                .Select(path => FileOperationItem.Failure(path, "无法创建目标目录"))
                .ToList());
        }

        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                continue;
            }

            try
            {
                items.Add(move ? MoveOne(sourceFile, destination) : CopyOne(sourceFile, destination));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"{(move ? "移动" : "复制")}文件失败 source={sourceFile} dest={destination} error={ex.Message}");
                items.Add(FileOperationItem.Failure(sourceFile, ex.Message));
            }
        }

        _logger.Info($"文件{(move ? "移动" : "复制")}完成 dest={destination} " +
                     $"ok={items.Count(i => i.Succeeded)} failed={items.Count(i => !i.Succeeded)}");
        return new FileOperationResult(items);
    }

    private FileOperationItem CopyOne(string sourceFile, string destinationDirectory)
    {
        if (!File.Exists(sourceFile))
        {
            return FileOperationItem.Failure(sourceFile, "源文件不存在");
        }

        if (FileOperationPlanner.IsSameDirectory(destinationDirectory, sourceFile))
        {
            return FileOperationItem.Failure(sourceFile, "源文件已在该目录中");
        }

        var target = FileOperationPlanner.ResolveTargetPath(destinationDirectory, sourceFile, File.Exists);
        File.Copy(sourceFile, target, overwrite: false);
        return FileOperationItem.Success(sourceFile, target);
    }

    private FileOperationItem MoveOne(string sourceFile, string destinationDirectory)
    {
        if (!File.Exists(sourceFile))
        {
            return FileOperationItem.Failure(sourceFile, "源文件不存在");
        }

        if (FileOperationPlanner.IsSameDirectory(destinationDirectory, sourceFile))
        {
            return FileOperationItem.Failure(sourceFile, "源文件已在该目录中");
        }

        var target = FileOperationPlanner.ResolveTargetPath(destinationDirectory, sourceFile, File.Exists);
        if (string.Equals(Path.GetPathRoot(sourceFile), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Move(sourceFile, target);
        }
        else
        {
            // 跨卷移动：File.Move 只允许同卷重命名，退化为复制 + 删除源文件。
            File.Copy(sourceFile, target, overwrite: false);
            File.Delete(sourceFile);
        }

        return FileOperationItem.Success(sourceFile, target);
    }

    private FileOperationResult DeleteToRecycleBin(IReadOnlyList<string> sourceFiles, CancellationToken cancellationToken)
    {
        var items = new List<FileOperationItem>(sourceFiles.Count);
        foreach (var sourceFile in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                continue;
            }

            try
            {
                if (!File.Exists(sourceFile))
                {
                    items.Add(FileOperationItem.Failure(sourceFile, "文件不存在"));
                    continue;
                }

                items.Add(_recycleBin.TryDelete(sourceFile)
                    ? FileOperationItem.Success(sourceFile, sourceFile)
                    : FileOperationItem.Failure(sourceFile, "无法移到回收站（文件可能被其他程序占用）"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warn($"删除到回收站失败 path={sourceFile} error={ex.Message}");
                items.Add(FileOperationItem.Failure(sourceFile, ex.Message));
            }
        }

        _logger.Info($"删除到回收站完成 ok={items.Count(i => i.Succeeded)} failed={items.Count(i => !i.Succeeded)}");
        return new FileOperationResult(items);
    }
}
