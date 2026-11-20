namespace EchoGarage.Infrastructure.Windows;

/// <summary>单个扩展名的注册状态。</summary>
public enum FileAssociationExtensionStatus
{
    /// <summary>ProgId 候选、<c>Applications\SupportedTypes</c> 与 <c>Capabilities\FileAssociations</c> 全部写入。</summary>
    Registered,

    /// <summary>
    /// 已通过 <c>Applications\SupportedTypes</c> 与 <c>Capabilities\FileAssociations</c> 注册，
    /// 但 <c>{扩展名}\OpenWithProgids</c> 写入被系统拒绝（该扩展名的注册表项可能是只读的）。
    /// Windows“默认应用”仍会列出 EchoGarage，只是少了一条旧式“打开方式”候选。
    /// </summary>
    Partial,

    /// <summary>Capabilities 或 SupportedTypes 写入失败，该格式无法作为候选打开方式出现。</summary>
    Failed,
}

/// <summary>一次注册尝试中单个扩展名的结果。</summary>
public sealed record FileAssociationExtensionResult(string Extension, FileAssociationExtensionStatus Status);

/// <summary>
/// 注册结果。ADR-008 规定注册只写 HKCU 且不覆盖默认处理程序；
/// 结果类型让对话框与日志能区分“完全成功 / 缺少 OpenWithProgids / 真正失败”，
/// 也用于验证“单个条目失败不中止其余格式”的容错行为。
/// </summary>
public sealed class FileAssociationResult
{
    private FileAssociationResult(
        bool executableAvailable,
        bool coreRegistered,
        IReadOnlyList<FileAssociationExtensionResult> extensions)
    {
        ExecutableAvailable = executableAvailable;
        CoreRegistered = coreRegistered;
        Entries = extensions;
    }

    /// <summary>调用方提供的 exe 是否存在（<c>Environment.ProcessPath</c> 为空时不可用）。</summary>
    public bool ExecutableAvailable { get; }

    /// <summary>ProgId、打开命令、Applications、RegisteredApplications 与 Capabilities 身份条目是否全部写入。</summary>
    public bool CoreRegistered { get; }

    public IReadOnlyList<FileAssociationExtensionResult> Entries { get; }

    public int SelectedCount => Entries.Count;

    /// <summary>所有条目都写入成功的格式。</summary>
    public IReadOnlyList<string> RegisteredExtensions =>
        Entries.Where(e => e.Status == FileAssociationExtensionStatus.Registered).Select(e => e.Extension).ToList();

    /// <summary>已注册但缺少 <c>OpenWithProgids</c> 条目的格式（注册表项被系统保护）。</summary>
    public IReadOnlyList<string> PartialExtensions =>
        Entries.Where(e => e.Status == FileAssociationExtensionStatus.Partial).Select(e => e.Extension).ToList();

    /// <summary>Capabilities / SupportedTypes 写入失败的格式。</summary>
    public IReadOnlyList<string> FailedExtensions =>
        Entries.Where(e => e.Status == FileAssociationExtensionStatus.Failed).Select(e => e.Extension).ToList();

    /// <summary>候选打开方式注册成功（可能仍缺少 OpenWithProgids，见 <see cref="PartialExtensions"/>）。</summary>
    public bool IsSuccess => ExecutableAvailable && CoreRegistered && SelectedCount > 0 && FailedExtensions.Count == 0;

    public static FileAssociationResult ExecutableUnavailable() => new(false, false, []);

    public static FileAssociationResult EmptySelection() => new(true, false, []);

    internal static FileAssociationResult Create(
        IReadOnlyList<FileAssociationExtensionResult> extensions,
        bool coreRegistered) => new(true, coreRegistered, extensions);
}

/// <summary>取消注册的结果。</summary>
public sealed class FileAssociationCleanupResult
{
    public FileAssociationCleanupResult(bool coreCleared, IReadOnlyList<string> lockedExtensions)
    {
        CoreCleared = coreCleared;
        LockedExtensions = lockedExtensions;
    }

    /// <summary>ProgId、Applications、RegisteredApplications 与 Capabilities 树是否清理完成。</summary>
    public bool CoreCleared { get; }

    /// <summary>仍残留 <c>OpenWithProgids</c> 值、但因注册表项只读而无法删除的扩展名。</summary>
    public IReadOnlyList<string> LockedExtensions { get; }

    public bool IsSuccess => CoreCleared && LockedExtensions.Count == 0;
}

/// <summary>当前 HKCU 中的注册状态快照，供对话框 / 设置页显示。</summary>
/// <param name="Extension">扩展名（带点）。</param>
/// <param name="Registered">Capabilities 中是否登记为 EchoGarage 的候选格式。</param>
/// <param name="OpenWithRegistered">是否写入了旧的 <c>OpenWithProgids</c> 候选条目。</param>
public sealed record FileAssociationState(string Extension, bool Registered, bool OpenWithRegistered);
