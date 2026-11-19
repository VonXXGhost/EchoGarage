namespace EchoGarage.Core.Models;

/// <summary>
/// 一次响度测量的结果（ADR-028 §3.2）：由 <see cref="EchoGarage.Core.Services.LoudnessMeter"/> 流式测量后汇总。
/// 不可测的量用 <c>null</c> 表示（例如短于 400 ms 没有积分响度、短于 3 s 没有 LRA / 短时响度）；
/// 静音文件的峰值是负无穷（<see cref="double.NegativeInfinity"/>），UI 展示时按“—”处理。
/// </summary>
/// <param name="IntegratedLufs">积分响度 I（LUFS）；无可测块时为 null。</param>
/// <param name="LoudnessRange">响度范围 LRA（LU）；短于 3 s 或数据点不足时为 null。</param>
/// <param name="TruePeakDb">真峰值（dBTP，BS.1770-5 4× 过采样估算）；静音为负无穷。</param>
/// <param name="SamplePeakDb">原始 PCM 采样峰值（dBFS）；静音为负无穷。</param>
/// <param name="MaxMomentaryLufs">Momentary（400 ms 窗）最大值（LUFS），不门限；没有完整块时为 null。</param>
/// <param name="MaxShortTermLufs">Short-term（3 s 窗）最大值（LUFS），不门限；短于 3 s 时为 null。</param>
/// <param name="SampleRate">解码后的采样率（Hz）。</param>
/// <param name="Channels">声道数。</param>
/// <param name="Duration">文件时长。</param>
/// <param name="Note">不可测原因等提示文案（例如「全曲静音」）；无提示时为 null。</param>
public sealed record LoudnessMeasurement(
    double? IntegratedLufs,
    double? LoudnessRange,
    double TruePeakDb,
    double SamplePeakDb,
    double? MaxMomentaryLufs,
    double? MaxShortTermLufs,
    int SampleRate,
    int Channels,
    TimeSpan Duration,
    string? Note);

/// <summary>
/// 单文件响度分析的进度（ADR-028 §3.2）。UI 用它驱动总进度条与「已处理时长」文案。
/// </summary>
/// <param name="Fraction">完成比例 0..1；无法获知总时长时为 -1。</param>
/// <param name="Processed">已解码分析的音频时长。</param>
/// <param name="Duration">文件总时长；无法获知时为 null。</param>
/// <param name="Stage">当前阶段文案（如「解码分析」）。</param>
public sealed record LoudnessAnalysisProgress(
    double Fraction,
    TimeSpan Processed,
    TimeSpan? Duration,
    string Stage);

/// <summary>
/// 一次响度调整的线性增益方案（ADR-028 §3.2 / KB-019 §9）：只做线性增益 + 真峰值天花板，不做限幅。
/// 提升时受「天花板 − 实测真峰值」限制，目标不可达时截断并给出 <see cref="TargetReachable"/> = false；
/// 降低时直接取目标增益，不受天花板限制。
/// </summary>
/// <param name="RequestedGainDb">目标增益 = 目标响度 − 实测响度（dB）。</param>
/// <param name="AppliedGainDb">实际增益 = 提升时 min(目标增益, 最大安全增益)，降低时等于目标增益。</param>
/// <param name="MaxSafeGainDb">无失真前提下最多可提升的增益 = 峰值天花板 − 实测真峰值（dB）。</param>
/// <param name="ResultingLufs">预计输出积分响度 = 实测响度 + 实际增益（LUFS）。</param>
/// <param name="ResultingTruePeakDb">预计输出真峰值 = 实测真峰值 + 实际增益（dBTP）。</param>
/// <param name="TargetReachable">实际增益与目标增益误差小于 0.05 dB 时为 true。</param>
public sealed record LoudnessAdjustmentPlan(
    double RequestedGainDb,
    double AppliedGainDb,
    double MaxSafeGainDb,
    double ResultingLufs,
    double ResultingTruePeakDb,
    bool TargetReachable);
