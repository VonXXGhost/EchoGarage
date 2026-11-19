using EchoGarage.Core.Models;

namespace EchoGarage.Core.Abstractions;

/// <summary>
/// 按文件测量响度（ADR-028 §3.2）：由 Infrastructure 的解码分析器实现，Core 只定义跨层契约。
/// 实现方负责解码为 float32、在后台线程分析、按块回报进度并响应取消；不写任何文件 / 数据库 / tag。
/// </summary>
public interface ILoudnessAnalyzer
{
    /// <summary>
    /// 分析一个音频文件的积分响度、LRA、真峰值与采样峰值。
    /// </summary>
    /// <param name="path">音频文件路径。</param>
    /// <param name="progress">可选的进度回调；实现方应 ≥5 Hz 回报一次。</param>
    /// <param name="cancellationToken">取消令牌；取消时应抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>测量结果；短文件 / 静音文件返回带 Note 的结果而不是抛异常。</returns>
    /// <remarks>文件不存在、无法读取或解码失败时，实现方抛出自定义异常（如 LoudnessAnalysisException）。</remarks>
    Task<LoudnessMeasurement> AnalyzeAsync(
        string path,
        IProgress<LoudnessAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
