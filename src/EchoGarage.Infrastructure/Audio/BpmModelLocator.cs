using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Configuration;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// 定位 Beat This! ONNX 模型文件（ADR-034 §2.2）：
/// <list type="bullet">
/// <item><c>small0</c>（内置）：优先查找程序目录下 <c>tools\models\beat_this_small0.onnx</c>，开发态向上回溯仓库根目录。</item>
/// <item><c>final0</c>（用户可选安装）：优先查找便携数据目录 <c>&lt;EchoGarageData&gt;\data\Models\beat_this_final0.onnx</c>
/// （覆盖更新程序不丢失），其次回退程序目录 <c>tools\models\</c>。</item>
/// </list>
/// </summary>
public sealed class BpmModelLocator
{
    /// <summary>低于此阈值视为不完整/HTML 错页下载，拒绝当作合法 ONNX 模型。</summary>
    public const long MinimumValidModelBytes = 1_000_000;

    private readonly AppPaths _paths;
    private readonly string _baseDirectory;

    public BpmModelLocator(AppPaths paths, string? baseDirectory = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
    }

    public string RecommendedFinal0Directory => _paths.ModelsDirectory;

    public BpmModelStatus GetStatus(BpmModelKind preferredModel)
    {
        var smallPath = LocateFile(BpmFormatter.Small0FileName, searchUserModelsFirst: false);
        var finalPath = LocateFile(BpmFormatter.Final0FileName, searchUserModelsFirst: true);

        var smallInfo = new BpmModelAvailability(
            BpmModelKind.Small0,
            "Beat This! Small（内置）",
            BpmFormatter.Small0FileName,
            smallPath is not null,
            smallPath,
            Path.Combine(_baseDirectory, "tools", "models"));

        var finalInfo = new BpmModelAvailability(
            BpmModelKind.Final0,
            "Beat This! Final（高精度）",
            BpmFormatter.Final0FileName,
            finalPath is not null,
            finalPath,
            _paths.ModelsDirectory);

        var effective = preferredModel == BpmModelKind.Final0 && finalInfo.Installed
            ? BpmModelKind.Final0
            : BpmModelKind.Small0;

        return new BpmModelStatus(smallInfo, finalInfo, effective);
    }

    public (BpmModelKind ActualKind, string ModelPath)? ResolveForInference(BpmModelKind preferredModel)
    {
        var status = GetStatus(preferredModel);
        if (preferredModel == BpmModelKind.Final0 && status.Final0.Installed && status.Final0.ResolvedPath is not null)
        {
            return (BpmModelKind.Final0, status.Final0.ResolvedPath);
        }

        if (status.Small0.Installed && status.Small0.ResolvedPath is not null)
        {
            return (BpmModelKind.Small0, status.Small0.ResolvedPath);
        }

        if (status.Final0.Installed && status.Final0.ResolvedPath is not null)
        {
            return (BpmModelKind.Final0, status.Final0.ResolvedPath);
        }

        return null;
    }

    private string? LocateFile(string fileName, bool searchUserModelsFirst)
    {
        var candidates = new List<string>();
        if (searchUserModelsFirst)
        {
            candidates.Add(Path.Combine(_paths.ModelsDirectory, fileName));
        }

        candidates.Add(Path.Combine(_baseDirectory, "tools", "models", fileName));
        candidates.Add(Path.Combine(_baseDirectory, "models", fileName));

        // 开发态（dotnet run / 单元测试在 bin/Debug/net10.0-windows 下运行）向上回溯仓库根的 tools\models。
        var dir = new DirectoryInfo(_baseDirectory);
        for (var depth = 0; depth < 6 && dir?.Parent is not null; depth++)
        {
            dir = dir.Parent;
            candidates.Add(Path.Combine(dir.FullName, "tools", "models", fileName));
        }

        if (!searchUserModelsFirst)
        {
            candidates.Add(Path.Combine(_paths.ModelsDirectory, fileName));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var info = new FileInfo(candidate);
                if (info.Exists && info.Length >= MinimumValidModelBytes)
                {
                    return info.FullName;
                }
            }
            catch
            {
                // Ignore inaccessible paths.
            }
        }

        return null;
    }
}
