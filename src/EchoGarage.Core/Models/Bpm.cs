namespace EchoGarage.Core.Models;

/// <summary>「正在播放」页当前展示的 BPM 数据来源（ADR-034 §5.2）。</summary>
public enum BpmSource
{
    None,
    /// <summary>来自音频文件内嵌标准标签（TBPM / BPM / tmpo / WM/BeatsPerMinute）。</summary>
    FileTag,
    /// <summary>来自本地 SQLite 缓存的算法测量结果（未写入音频文件）。</summary>
    CachedMeasured,
    /// <summary>来自本地 SQLite 缓存的用户手动修正结果（未写入音频文件，受清空缓存保护）。</summary>
    CachedManual
}

/// <summary>Beat This! ONNX 模型档位（ADR-034 §2）。</summary>
public enum BpmModelKind
{
    /// <summary>随包内置的紧凑高泛化模型（beat_this_small0.onnx，约 10.5 MB）。</summary>
    Small0,
    /// <summary>用户按设置页指引自行安装的高精度主模型（beat_this_final0.onnx，约 80 MB）。</summary>
    Final0
}

/// <summary>一个候选 BPM 及其相对得分（用于人工核对与快捷点选）。</summary>
public sealed record BpmCandidate(double Bpm, double Score);

/// <summary>变速曲目的单个稳定速度段落（按时间顺序排列，用于进度条比例可视化与明细列表）。</summary>
public sealed record BpmSegment(
    double StartSeconds,
    double EndSeconds,
    double Bpm,
    double Confidence,
    bool IsPrimary = false);

/// <summary>单次 BPM 算法分析的完整结果。</summary>
public sealed record BpmAnalysisResult(
    double Bpm,
    double Confidence,
    IReadOnlyList<BpmCandidate> Candidates,
    bool IsVariableTempoSuspected,
    string AlgorithmId,
    int AlgorithmVersion,
    string ExecutionProvider,
    TimeSpan Elapsed,
    bool IsReliable = true,
    string? Note = null)
{
    /// <summary>检出的稳定速度段落（仅 2–5 段时填充；单段恒速或超过 5 段时为空）。</summary>
    public IReadOnlyList<BpmSegment> Segments { get; init; } = [];

    /// <summary>算法检出的速度段落总数（0 表示旧缓存未计算，1 表示恒速，&gt;5 表示变速过多）。</summary>
    public int DetectedSegmentCount { get; init; }

    /// <summary>是否因段落数量超过展示上限（&gt; 5 段）或连续自由拍而隐藏段落明细。</summary>
    public bool TooManyTempoChanges { get; init; }
}

/// <summary>BPM 后台分析的阶段与进度（0..1，未知时为 -1）。</summary>
public sealed record BpmAnalysisProgress(double Fraction, string Stage);

/// <summary>SQLite <c>track_bpm_cache</c> 表的一条持久化记录（schema v6）。</summary>
public sealed class BpmCacheEntry
{
    public string Path { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime ModifiedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public double? MeasuredBpm { get; set; }
    public double? Confidence { get; set; }
    public bool IsVariableTempo { get; set; }
    public IReadOnlyList<BpmCandidate> Candidates { get; set; } = [];
    public IReadOnlyList<BpmSegment> Segments { get; set; } = [];
    public int DetectedSegmentCount { get; set; }
    public string? AnalyzerId { get; set; }
    public int AnalyzerVersion { get; set; }
    public double? ManualBpm { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>「正在播放」页与修正浮层所需的合并后 BPM 展示状态。</summary>
public sealed record BpmDisplayState(
    double? EffectiveBpm,
    BpmSource Source,
    double? FileTagBpm,
    double? MeasuredBpm,
    double? ManualBpm,
    double? Confidence,
    bool IsVariableTempoSuspected,
    bool DurationChangedWarning,
    bool ExternalFileTagChangedHint,
    IReadOnlyList<BpmCandidate> Candidates,
    string? AnalyzerId,
    string? ExecutionProvider = null)
{
    /// <summary>变速曲目的分段明细（仅 2–5 段时有值；超过 5 段时置空并标记 <see cref="TooManyTempoChanges"/>）。</summary>
    public IReadOnlyList<BpmSegment> Segments { get; init; } = [];

    /// <summary>算法检出的速度段落数（0=未分析/旧缓存，1=恒速，2..5=有效分段，&gt;5=变速过多）。</summary>
    public int DetectedSegmentCount { get; init; }

    /// <summary>段落数超过展示上限（&gt; 5）时为 true，前端展示提示并隐藏段落明细。</summary>
    public bool TooManyTempoChanges { get; init; }

    public static BpmDisplayState Empty { get; } = new(
        null, BpmSource.None, null, null, null, null, false, false, false, [], null);
}

/// <summary>单个 ONNX 模型的本地安装状态（供设置页展示与切换）。</summary>
public sealed record BpmModelAvailability(
    BpmModelKind Kind,
    string DisplayName,
    string ExpectedFileName,
    bool Installed,
    string? ResolvedPath,
    string RecommendedInstallDirectory);

/// <summary>内置 small0 与可选 final0 模型的整体状态。</summary>
public sealed record BpmModelStatus(
    BpmModelAvailability Small0,
    BpmModelAvailability Final0,
    BpmModelKind EffectiveModel);

/// <summary>BPM 缓存统计（供设置页「清空缓存 ▾」显示条目数）。</summary>
public sealed record BpmCacheStats(int TotalCount, int MeasuredCount, int ManualCount);

/// <summary>BPM 测量与展示设置（持久化到 settings.json，向后兼容）。</summary>
public sealed class BpmSettings
{
    public BpmModelKind PreferredModel { get; set; } = BpmModelKind.Small0;

    /// <summary>修正 BPM 浮层打开时「同时写入音频文件标签」的默认勾选状态（默认关闭，保护原文件）。</summary>
    public bool WriteToFileByDefault { get; set; }

    /// <summary>界面显示小数位数：1（默认，如 128.0）或 0（整数，如 128）。</summary>
    public int DecimalPlaces { get; set; } = 1;

    /// <summary>优先启用 DirectML（Windows DX12）GPU 加速，不可用或失败时自动回退 CPU。</summary>
    public bool UseGpuAcceleration { get; set; } = true;

    /// <summary>测量完成后空闲卸载 ONNX 会话释放显存/内存的秒数；0 表示测完立即释放，默认 60。</summary>
    public int IdleUnloadSeconds { get; set; } = 60;

    /// <summary>写入 ID3v2 TBPM 时是否强制取整（兼容少数严格按旧规范只认整数的硬件设备）。</summary>
    public bool RoundId3BpmToInteger { get; set; }

    public static BpmSettings Normalize(BpmSettings? settings)
    {
        settings ??= new BpmSettings();
        if (!Enum.IsDefined(settings.PreferredModel))
        {
            settings.PreferredModel = BpmModelKind.Small0;
        }

        settings.DecimalPlaces = settings.DecimalPlaces <= 0 ? 0 : 1;
        settings.IdleUnloadSeconds = settings.IdleUnloadSeconds switch
        {
            <= 0 => 0,
            <= 30 => 30,
            <= 60 => 60,
            <= 300 => 300,
            _ => 300
        };
        return settings;
    }
}
