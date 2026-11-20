using EchoGarage.Core.Abstractions;
using Microsoft.Win32;

namespace EchoGarage.Infrastructure.Windows;

/// <summary>
/// Registers EchoGarage in HKCU so Explorer can offer it as a handler for audio files.
/// Windows 10/11 protects the user's chosen default handler, so registration writes the
/// ProgId + OpenWith entries and then guides the user to the system default-apps page.
/// Identifiers and the registry backend are injectable so tests can verify every key path.
///
/// 注册是逐条目容错的：某些扩展名的注册表项可能被安全软件 / 其他播放器改成只读
/// （本机实测 .flac / .mp3 / .mkv / .mp4 的 <c>HKCU\Software\Classes\{ext}</c> 只授予 KEY_READ），
/// 写不进去时只跳过该条目并记录原因，不能中止其余格式或让整个功能表现为“不可用”。
/// </summary>
public sealed class FileAssociationService
{
    public const string DefaultProgId = "EchoGarage.AudioFile";
    public const string DefaultApplicationName = "EchoGarage.exe";
    public const string DefaultDisplayName = "EchoGarage";

    public static readonly IReadOnlyList<string> AudioExtensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".aiff", ".wma", ".mp4"];

    private readonly IRegistryStore _store;
    private readonly IAppLogger? _logger;

    public FileAssociationService(
        string progId = DefaultProgId,
        string applicationName = DefaultApplicationName,
        string displayName = DefaultDisplayName,
        IRegistryStore? store = null,
        IAppLogger? logger = null)
    {
        ProgId = progId;
        ApplicationName = applicationName;
        DisplayName = displayName;
        _store = store ?? new WindowsRegistryStore();
        _logger = logger;
    }

    public string ProgId { get; }
    public string ApplicationName { get; }
    public string DisplayName { get; }

    private string ProgIdPath => $@"Software\Classes\{ProgId}";
    private string CapabilitiesPath => $@"Software\{ProgId}\Capabilities";
    private string SupportedTypesPath => $@"Software\Classes\Applications\{ApplicationName}\SupportedTypes";
    private string FileAssociationsPath => $@"Software\{ProgId}\Capabilities\FileAssociations";

    public bool IsRegistered() => _store.KeyExists($@"{ProgIdPath}\shell\open\command");

    /// <summary>
    /// 当前注册状态（只读）：哪些格式登记在 Capabilities 里、哪些还写入了 OpenWithProgids。
    /// </summary>
    public IReadOnlyList<FileAssociationState> GetRegistrationState() =>
        AudioExtensions
            .Select(extension => new FileAssociationState(
                extension,
                Registered: string.Equals(
                    _store.GetValue(FileAssociationsPath, extension) as string,
                    ProgId,
                    StringComparison.OrdinalIgnoreCase),
                OpenWithRegistered: _store.GetValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId) is not null))
            .ToList();

    /// <summary>
    /// Registers the ProgId for the selected extensions only. Registration is always an
    /// explicit user action (see the association dialog); the app never registers itself on
    /// startup, and it never overwrites the extension's current default handler.
    /// </summary>
    public FileAssociationResult Register(string executablePath, IReadOnlyList<string>? extensions = null)
    {
        var selected = NormalizeExtensions(extensions);
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            _logger?.Warn($"文件关联：注册中止，程序路径不可用（{executablePath ?? "<null>"}）。");
            return FileAssociationResult.ExecutableUnavailable();
        }

        if (selected.Count == 0)
        {
            _logger?.Warn("文件关联：注册中止，没有选择任何受支持的格式。");
            return FileAssociationResult.EmptySelection();
        }

        _logger?.Info(
            $"文件关联：开始注册 {selected.Count} 种格式（{string.Join(", ", selected)}），程序={executablePath}");

        var command = $"\"{executablePath}\" \"%1\"";
        var icon = $"\"{executablePath}\",0";

        var coreEntries = new (string KeyPath, string? Name, string Value, string Label)[]
        {
            (ProgIdPath, null, $"{DisplayName} 音乐文件", "ProgId 描述"),
            ($@"{ProgIdPath}\DefaultIcon", null, icon, "DefaultIcon"),
            ($@"{ProgIdPath}\shell\open\command", null, command, "ProgId 打开命令"),
            ($@"Software\Classes\Applications\{ApplicationName}", "FriendlyAppName", DisplayName, "Applications FriendlyAppName"),
            ($@"Software\Classes\Applications\{ApplicationName}\shell\open\command", null, command, "Applications 打开命令"),
            (@"Software\RegisteredApplications", DisplayName, CapabilitiesPath, "RegisteredApplications"),
            (CapabilitiesPath, "ApplicationName", DisplayName, "Capabilities ApplicationName"),
            (CapabilitiesPath, "ApplicationDescription", $"{DisplayName} 本地音乐播放器", "Capabilities ApplicationDescription"),
        };

        // 身份类条目失败也继续往下写：把能写的都写进去，后面用结构化结果告诉用户缺什么。
        var coreFailures = 0;
        foreach (var (keyPath, name, value, label) in coreEntries)
        {
            if (_store.SetValue(keyPath, name, value))
            {
                continue;
            }

            coreFailures++;
            _logger?.Warn(
                $"文件关联：写入 {label} 失败（HKCU\\{keyPath}），{DescribeStoreFailure()}");
        }

        var entries = new List<FileAssociationExtensionResult>(selected.Count);
        foreach (var extension in selected)
        {
            // Capabilities 与 SupportedTypes 是 Windows 11“默认应用 / 打开方式推荐列表”的主要来源；
            // 本机实测：只要这两处写入成功，即使 OpenWithProgids 被拒绝，EchoGarage 仍会出现在推荐列表里。
            var supportedTypes = _store.SetValue(SupportedTypesPath, extension, string.Empty);
            if (!supportedTypes)
            {
                _logger?.Warn(
                    $"文件关联：{extension} 的 Applications\\SupportedTypes 写入失败，{DescribeStoreFailure()}");
            }

            var capabilities = _store.SetValue(FileAssociationsPath, extension, ProgId);
            if (!capabilities)
            {
                _logger?.Warn(
                    $"文件关联：{extension} 的 Capabilities\\FileAssociations 写入失败，{DescribeStoreFailure()}");
            }

            // 旧式候选列表（“打开方式”菜单）。只读的扩展名键会拒绝创建 OpenWithProgids 子键，
            // 这不应影响注册结果——只是少一条候选，可通过“默认应用”页仍然选择 EchoGarage。
            var openWith = _store.SetValue($@"Software\Classes\{extension}\OpenWithProgids", ProgId, string.Empty);
            if (!openWith)
            {
                _logger?.Warn(
                    $"文件关联：{extension} 的 OpenWithProgids 写入被拒绝（HKCU\\Software\\Classes\\{extension} 可能是只读的），" +
                    $"已改用 Applications\\SupportedTypes + Capabilities 注册；{DescribeStoreFailure()}");
            }

            var status = supportedTypes && capabilities
                ? (openWith ? FileAssociationExtensionStatus.Registered : FileAssociationExtensionStatus.Partial)
                : FileAssociationExtensionStatus.Failed;
            entries.Add(new FileAssociationExtensionResult(extension, status));
        }

        // Re-registering with a smaller selection removes the formats the user unchecked.
        foreach (var extension in AudioExtensions.Where(e =>
                     !selected.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            RemoveValueIfPresent($@"Software\Classes\{extension}\OpenWithProgids", ProgId, $"{extension} 的 OpenWithProgids");
            RemoveValueIfPresent(FileAssociationsPath, extension, $"{extension} 的 Capabilities 条目");
            RemoveValueIfPresent(SupportedTypesPath, extension, $"{extension} 的 SupportedTypes 条目");
        }

        var result = FileAssociationResult.Create(entries, coreFailures == 0);
        if (result.FailedExtensions.Count == 0 && result.PartialExtensions.Count == 0)
        {
            _logger?.Info($"文件关联：注册完成，{result.SelectedCount} 种格式全部写入。");
        }
        else
        {
            _logger?.Warn(
                "文件关联：注册完成（可部分使用）：" +
                $"成功 {result.RegisteredExtensions.Count}、缺少 OpenWithProgids {result.PartialExtensions.Count}" +
                $"（{string.Join(", ", result.PartialExtensions)}）、失败 {result.FailedExtensions.Count}" +
                $"（{string.Join(", ", result.FailedExtensions)}）。");
        }

        return result;
    }

    public static IReadOnlyList<string> NormalizeExtensions(IReadOnlyList<string>? extensions)
    {
        if (extensions is null)
        {
            return AudioExtensions;
        }

        var result = new List<string>();
        foreach (var extension in extensions)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                continue;
            }

            var normalized = extension.Trim();
            if (!normalized.StartsWith('.'))
            {
                normalized = "." + normalized;
            }

            var match = AudioExtensions.FirstOrDefault(e =>
                string.Equals(e, normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !result.Contains(match, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(match);
            }
        }

        return result;
    }

    public FileAssociationCleanupResult Unregister()
    {
        var coreCleared = true;
        coreCleared &= _store.DeleteTree(ProgIdPath);
        coreCleared &= _store.DeleteTree($@"Software\Classes\Applications\{ApplicationName}");
        coreCleared &= _store.DeleteValue(@"Software\RegisteredApplications", DisplayName);
        coreCleared &= _store.DeleteTree($@"Software\{ProgId}");

        // 只在值确实存在时才删除：只读扩展名键连 DeleteValue 都会因打不开可写句柄而失败，
        // 但那时本来也没有我们的值，不应报成清理失败。
        var lockedExtensions = new List<string>();
        foreach (var extension in AudioExtensions)
        {
            var keyPath = $@"Software\Classes\{extension}\OpenWithProgids";
            if (_store.GetValue(keyPath, ProgId) is null)
            {
                continue;
            }

            if (_store.DeleteValue(keyPath, ProgId))
            {
                continue;
            }

            lockedExtensions.Add(extension);
            _logger?.Warn(
                $"文件关联：清理 {extension} 的 OpenWithProgids 失败（注册表项只读），{DescribeStoreFailure()}");
        }

        var result = new FileAssociationCleanupResult(coreCleared, lockedExtensions);
        if (coreCleared && lockedExtensions.Count == 0)
        {
            _logger?.Info("文件关联：已清理全部注册条目。");
        }
        else
        {
            _logger?.Warn(
                $"文件关联：取消注册未完全成功（核心清理={(coreCleared ? "成功" : "失败")}，" +
                $"残留格式={string.Join(", ", lockedExtensions)}）。");
        }

        return result;
    }

    /// <summary>值不存在时直接视为“无需清理”，避免只读键产生假失败。</summary>
    private void RemoveValueIfPresent(string keyPath, string? valueName, string label)
    {
        if (_store.GetValue(keyPath, valueName) is null)
        {
            return;
        }

        if (!_store.DeleteValue(keyPath, valueName))
        {
            _logger?.Warn($"文件关联：清理 {label} 失败（HKCU\\{keyPath}），{DescribeStoreFailure()}");
        }
    }

    private string DescribeStoreFailure() =>
        $"原因：{_store.LastError ?? "未知（注册表项可能只读或不存在）"}。";

    public static void OpenDefaultAppsSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:defaultapps",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore: the user can open Settings manually.
        }
    }
}
