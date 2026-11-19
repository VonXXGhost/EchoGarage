using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-028 §4.1：<see cref="LoudnessGain"/> 的增益规划——提升受真峰值天花板限制、降低全量应用、
/// 无损 / 有损输出使用不同的天花板。
/// </summary>
public sealed class LoudnessGainTests
{
    [Fact]
    public void CeilingDbTp_MatchesAdrConstants()
    {
        Assert.Equal(-1.0, LoudnessGain.LosslessCeilingDbTp);
        Assert.Equal(-2.0, LoudnessGain.LossyCeilingDbTp);
        Assert.Equal(-1.0, LoudnessGain.CeilingDbTp(lossyFormat: false));
        Assert.Equal(-2.0, LoudnessGain.CeilingDbTp(lossyFormat: true));
    }

    [Fact]
    public void MaxSafeGainDb_IsCeilingMinusTruePeak()
    {
        Assert.Equal(2.0, LoudnessGain.MaxSafeGainDb(-3.0, -1.0), 9);
        Assert.Equal(-2.0, LoudnessGain.MaxSafeGainDb(0.0, -2.0), 9);
        Assert.Equal(0.0, LoudnessGain.MaxSafeGainDb(-1.0, -1.0), 9);
    }

    [Fact]
    public void Plan_BoostBeyondCeiling_IsTruncatedAndReportedUnreachable()
    {
        // TP=-3、天花板 -1 → 最多 +2 dB；目标 +6 dB 不可达。
        var plan = LoudnessGain.Plan(
            measuredLufs: -20.0,
            measuredTruePeakDb: -3.0,
            targetLufs: -14.0,
            ceilingDbTp: -1.0);

        Assert.Equal(6.0, plan.RequestedGainDb, 9);
        Assert.Equal(2.0, plan.MaxSafeGainDb, 9);
        Assert.Equal(2.0, plan.AppliedGainDb, 9);
        Assert.Equal(-18.0, plan.ResultingLufs, 9);
        Assert.Equal(-1.0, plan.ResultingTruePeakDb, 9);
        Assert.False(plan.TargetReachable);
    }

    [Fact]
    public void Plan_BoostWithinCeiling_IsFullyApplied()
    {
        var plan = LoudnessGain.Plan(
            measuredLufs: -20.0,
            measuredTruePeakDb: -3.0,
            targetLufs: -19.0,
            ceilingDbTp: -1.0);

        Assert.Equal(1.0, plan.RequestedGainDb, 9);
        Assert.Equal(1.0, plan.AppliedGainDb, 9);
        Assert.Equal(-19.0, plan.ResultingLufs, 9);
        Assert.Equal(-2.0, plan.ResultingTruePeakDb, 9);
        Assert.True(plan.TargetReachable);
    }

    [Fact]
    public void Plan_Reduction_IsAppliedInFull_EvenWithoutHeadroom()
    {
        // 衰减不受天花板限制：TP=0 已超 -1 天花板，仍按目标 -3 dB 全量衰减。
        var plan = LoudnessGain.Plan(
            measuredLufs: -10.0,
            measuredTruePeakDb: 0.0,
            targetLufs: -13.0,
            ceilingDbTp: -1.0);

        Assert.Equal(-3.0, plan.RequestedGainDb, 9);
        Assert.Equal(-1.0, plan.MaxSafeGainDb, 9);
        Assert.Equal(-3.0, plan.AppliedGainDb, 9);
        Assert.Equal(-13.0, plan.ResultingLufs, 9);
        Assert.Equal(-3.0, plan.ResultingTruePeakDb, 9);
        Assert.True(plan.TargetReachable);
    }

    [Fact]
    public void Plan_AlreadyAboveCeiling_CannotBoostAtAll()
    {
        // 实测 TP 已经高于天花板 → 最大安全增益为负，提升请求只能被截成负增益。
        var plan = LoudnessGain.Plan(
            measuredLufs: -20.0,
            measuredTruePeakDb: 0.0,
            targetLufs: -14.0,
            ceilingDbTp: -1.0);

        Assert.Equal(6.0, plan.RequestedGainDb, 9);
        Assert.Equal(-1.0, plan.MaxSafeGainDb, 9);
        Assert.Equal(-1.0, plan.AppliedGainDb, 9);
        Assert.Equal(-21.0, plan.ResultingLufs, 9);
        Assert.Equal(-1.0, plan.ResultingTruePeakDb, 9);
        Assert.False(plan.TargetReachable);
    }

    [Fact]
    public void Plan_AlreadyAtTarget_IsNoOpAndReachable()
    {
        var plan = LoudnessGain.Plan(
            measuredLufs: -14.0,
            measuredTruePeakDb: -1.0,
            targetLufs: -14.0,
            ceilingDbTp: -1.0);

        Assert.Equal(0.0, plan.RequestedGainDb, 9);
        Assert.Equal(0.0, plan.AppliedGainDb, 9);
        Assert.Equal(-14.0, plan.ResultingLufs, 9);
        Assert.Equal(-1.0, plan.ResultingTruePeakDb, 9);
        Assert.True(plan.TargetReachable);
    }

    [Fact]
    public void Plan_LossyCeiling_LeavesOneMoreDbOfHeadroom()
    {
        var lossless = LoudnessGain.Plan(-20.0, -3.0, -14.0, LoudnessGain.CeilingDbTp(lossyFormat: false));
        var lossy = LoudnessGain.Plan(-20.0, -3.0, -14.0, LoudnessGain.CeilingDbTp(lossyFormat: true));

        Assert.Equal(2.0, lossless.AppliedGainDb, 9);
        Assert.Equal(1.0, lossy.AppliedGainDb, 9);
        Assert.Equal(-2.0, lossy.ResultingTruePeakDb, 9);
        Assert.False(lossy.TargetReachable);
    }
}
