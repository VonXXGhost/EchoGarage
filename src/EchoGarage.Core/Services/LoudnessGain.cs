using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 响度调整的增益规划（ADR-028 §3.2）：纯函数、无副作用，便于单元测试。
///
/// 口径与 ffmpeg `loudnorm linear=true` / ReplayGain 一致：增益是常数线性缩放，不做限幅；
/// **提升**受真峰值天花板限制（`AppliedGainDb = min(目标增益, 天花板 − 实测真峰值)`），
/// **降低**不受限制（直接取目标增益），不会因为峰值余量额外多降。
/// </summary>
public static class LoudnessGain
{
    /// <summary>无损输出（FLAC / WAV）的真峰值天花板（dBTP）：覆盖重采样与编码后的少量过冲。</summary>
    public const double LosslessCeilingDbTp = -1.0;

    /// <summary>有损输出（MP3 / AAC）的真峰值天花板（dBTP）：再留 1 dB 给编码过冲。</summary>
    public const double LossyCeilingDbTp = -2.0;

    /// <summary>
    /// 按输出格式返回真峰值天花板（dBTP）。
    /// </summary>
    /// <param name="lossyFormat">是否为 MP3 / AAC 等有损编码输出。</param>
    /// <returns>-2.0（有损）或 -1.0（无损）。</returns>
    public static double CeilingDbTp(bool lossyFormat) =>
        lossyFormat ? LossyCeilingDbTp : LosslessCeilingDbTp;

    /// <summary>
    /// 计算实测真峰值下的最大安全提升量（dB）。负值表示当前信号已超过天花板。
    /// </summary>
    /// <param name="measuredTruePeakDb">实测真峰值（dBTP）。</param>
    /// <param name="ceilingDbTp">目标格式的真峰值天花板（dBTP）。</param>
    /// <returns><paramref name="ceilingDbTp"/> − <paramref name="measuredTruePeakDb"/>。</returns>
    public static double MaxSafeGainDb(double measuredTruePeakDb, double ceilingDbTp) =>
        ceilingDbTp - measuredTruePeakDb;

    /// <summary>
    /// 生成一次响度调整方案。
    /// </summary>
    /// <param name="measuredLufs">实测积分响度（LUFS）。</param>
    /// <param name="measuredTruePeakDb">实测真峰值（dBTP）。</param>
    /// <param name="targetLufs">目标积分响度（LUFS）。</param>
    /// <param name="ceilingDbTp">目标格式的真峰值天花板（dBTP），见 <see cref="CeilingDbTp"/>。</param>
    /// <returns>调整方案；提升被截断时 <see cref="LoudnessAdjustmentPlan.TargetReachable"/> 为 false。</returns>
    public static LoudnessAdjustmentPlan Plan(
        double measuredLufs,
        double measuredTruePeakDb,
        double targetLufs,
        double ceilingDbTp)
    {
        var requestedGainDb = targetLufs - measuredLufs;
        var maxSafeGainDb = MaxSafeGainDb(measuredTruePeakDb, ceilingDbTp);

        // 只有提升需要受峰值上限约束；衰减是全量应用（目标增益 ≤ 0 dB）。
        var appliedGainDb = requestedGainDb > 0.0
            ? Math.Min(requestedGainDb, maxSafeGainDb)
            : requestedGainDb;

        // 允许亚 0.05 dB 的浮点/舍入误差，避免 -10.0 与 -9.999999 被误判为不可达。
        var targetReachable = Math.Abs(appliedGainDb - requestedGainDb) < 0.05;

        return new LoudnessAdjustmentPlan(
            requestedGainDb,
            appliedGainDb,
            maxSafeGainDb,
            measuredLufs + appliedGainDb,
            measuredTruePeakDb + appliedGainDb,
            targetReachable);
    }
}
