using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// ITU-R BS.1770-5 / EBU R 128 的流式响度表（ADR-028 §3.2；算法与验证细节见 KB-019）。
///
/// 纯 C# 实现（不引用 NAudio / WPF）：调用方按交错 float32 采样流式喂入，<see cref="Complete"/>
/// 汇总积分响度 I、响度范围 LRA、真峰值 TP、采样峰值与 M/S 最大值。分析过程只保留滚动 hop 和、
/// 每个 100 ms 的加权块能量与短时 S 序列（约 160 B/s），不需要整曲 PCM。
///
/// 算法口径：
/// <list type="bullet">
/// <item>K 加权：48 kHz 用 BS.1770-5 Table 1/2 官方系数的解析设计公式（任意采样率现场生成），每声道两级 biquad 级联，状态跨块连续。</item>
/// <item>积分响度：400 ms 块 / 100 ms 步进（75% 重叠）；-70 LUFS 绝对门限 + 「绝对门限后响度 −10 LU」相对门限，能量域聚合。</item>
/// <item>LRA：3 s 短时窗 / 100 ms 步进；-70 LUFS 绝对门限 + −20 LU 相对门限；P10/P95 最近秩（不插值）。</item>
/// <item>True Peak：BS.1770-5 Annex 2 的 4× 48-tap（4 相 × 12）ITU 官方系数，对原始 PCM 计算（不是 K 加权信号）。</item>
/// </list>
/// </summary>
public sealed class LoudnessMeter
{
    /// <summary>BS.1770-5 eq.2 的响度标定偏移：正好抵消 997 Hz 处 K 加权 +0.691 dB 的增益。</summary>
    private const double LoudnessOffset = -0.691;

    /// <summary>积分响度绝对门限（LUFS）。</summary>
    private const double AbsoluteGateLufs = -70.0;

    /// <summary>积分响度相对门限（相对「绝对门限后的响度」，LU）。</summary>
    private const double IntegratedRelativeGateLu = -10.0;

    /// <summary>LRA 相对门限（相对「绝对门限后的短时响度均值」，LU）。</summary>
    private const double LoudnessRangeRelativeGateLu = -20.0;

    /// <summary>400 ms 块包含的 hop 数（hop = 100 ms）。</summary>
    private const int BlockHopCount = 4;

    /// <summary>3 s 短时窗包含的 hop 数。</summary>
    private const int ShortTermHopCount = 30;

    /// <summary>真峰值插值滤波器的抽头数（每相 12 个，4 相共 48 个）。</summary>
    private const int TruePeakTaps = 12;

    /// <summary>K 加权状态低于该值时清零：连续静音会让 biquad 进入 denormal 区间，拖慢后续运算。</summary>
    private const double DenormalThreshold = 1e-12;

    /// <summary>BS.1770-5 Annex 2 Table 6：4× 过采样的 4 相 12 抽头系数（Phase 0）。</summary>
    private static readonly double[] TruePeakPhase0 =
    [
        0.0017089843750, 0.0109863281250, -0.0196533203125, 0.0332031250000,
        -0.0594482421875, 0.1373291015625, 0.9721679687500, -0.1022949218750,
        0.0476074218750, -0.0266113281250, 0.0148925781250, -0.0083007812500,
    ];

    /// <summary>BS.1770-5 Annex 2 Table 6：Phase 1。</summary>
    private static readonly double[] TruePeakPhase1 =
    [
        -0.0291748046875, 0.0292968750000, -0.0517578125000, 0.0891113281250,
        -0.1665039062500, 0.4650878906250, 0.7797851562500, -0.2003173828125,
        0.1015625000000, -0.0582275390625, 0.0330810546875, -0.0189208984375,
    ];

    /// <summary>BS.1770-5 Annex 2 Table 6：Phase 2。</summary>
    private static readonly double[] TruePeakPhase2 =
    [
        -0.0189208984375, 0.0330810546875, -0.0582275390625, 0.1015625000000,
        -0.2003173828125, 0.7797851562500, 0.4650878906250, -0.1665039062500,
        0.0891113281250, -0.0517578125000, 0.0292968750000, -0.0291748046875,
    ];

    /// <summary>BS.1770-5 Annex 2 Table 6：Phase 3（= Phase 0 的反转）。</summary>
    private static readonly double[] TruePeakPhase3 =
    [
        -0.0083007812500, 0.0148925781250, -0.0266113281250, 0.0476074218750,
        -0.1022949218750, 0.9721679687500, 0.1373291015625, -0.0594482421875,
        0.0332031250000, -0.0196533203125, 0.0109863281250, 0.0017089843750,
    ];

    private readonly int _sampleRate;
    private readonly int _channels;
    private readonly double[] _channelWeights;
    private readonly bool _enableTruePeak;

    /// <summary>100 ms 步进的样本数：hop = (fs + 5) / 10（四舍五入，与 libebur128 一致）。</summary>
    private readonly int _hopSamples;

    /// <summary>400 ms 块的样本数：block = 4 × hop。</summary>
    private readonly int _blockSamples;

    /// <summary>3 s 短时窗的样本数：30 × hop。</summary>
    private readonly int _shortTermSamples;

    private readonly BiquadCoefficients _preFilter;
    private readonly BiquadCoefficients _rlbFilter;

    /// <summary>每声道第一级（pre-filter）的滤波器状态。</summary>
    private readonly FilterState[] _preStates;

    /// <summary>每声道第二级（RLB 高通）的滤波器状态。</summary>
    private readonly FilterState[] _rlbStates;

    /// <summary>当前 hop 内每声道的 K 加权平方和。</summary>
    private readonly double[] _hopSums;

    /// <summary>最近 30 个 hop 的每声道平方和环形缓冲（同时供 400 ms 块与 3 s 短时窗使用）。</summary>
    private readonly double[][] _hopHistory;

    /// <summary>真峰值滤波器每声道的最近 12 个原始采样（环形缓冲）。</summary>
    private readonly double[][] _truePeakHistory;

    /// <summary>积分响度的门限块能量：每个 100 ms 步进一个 Σ G_i·z_ij（能量域，不含 -0.691 偏移）。</summary>
    private readonly List<double> _blockEnergies = new(1024);

    /// <summary>通过 -70 LUFS 绝对门限的短时响度 S 序列（LUFS），供 LRA 两遍门限与百分位使用。</summary>
    private readonly List<double> _shortTermLoudness = new(1024);

    private int _hopFill;
    private int _hopWriteIndex;
    private int _completedHops;
    private int _truePeakWriteIndex;
    private long _processedFrames;
    private double _samplePeak;
    private double _truePeak;
    private double? _maxMomentaryLufs;
    private double? _maxShortTermLufs;

    /// <summary>
    /// K 加权滤波器的双二阶系数，约定 H(z) = (b0 + b1·z⁻¹ + b2·z⁻²) / (1 + a1·z⁻¹ + a2·z⁻²)。
    /// </summary>
    /// <param name="B0">分子 b0。</param>
    /// <param name="B1">分子 b1。</param>
    /// <param name="B2">分子 b2。</param>
    /// <param name="A1">分母 a1（已含符号，与 BS.1770-5 表一致）。</param>
    /// <param name="A2">分母 a2（已含符号，与 BS.1770-5 表一致）。</param>
    public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2);

    /// <summary>K 加权的两级滤波器系数（第一级 pre-filter，第二级 RLB 高通）。</summary>
    /// <param name="PreFilter">第一级 pre-filter。</param>
    /// <param name="RlbFilter">第二级 RLB 高通。</param>
    public readonly record struct KWeightingCoefficients(BiquadCoefficients PreFilter, BiquadCoefficients RlbFilter);

    /// <summary>
    /// 创建一个流式响度表。
    /// </summary>
    /// <param name="sampleRate">解码后的采样率（Hz），建议 8000 及以上。</param>
    /// <param name="channelWeights">
    /// 每声道能量权重（按文件声道顺序，由上层按 ChannelMask / 声道数解析）：
    /// L/R/C = 1.0，Ls/Rs/侧/后环绕 = 1.41，LFE = 0。
    /// </param>
    /// <param name="enableTruePeak">是否计算真峰值（4×48-tap FIR，约 48 MAC/样本/声道）；关闭时结果回退为采样峰值。</param>
    public LoudnessMeter(int sampleRate, IReadOnlyList<double> channelWeights, bool enableTruePeak = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(channelWeights);
        if (channelWeights.Count == 0)
        {
            throw new ArgumentException("至少需要一个声道权重。", nameof(channelWeights));
        }

        if (sampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率低于 8000 Hz，不是有效音频。");
        }

        _sampleRate = sampleRate;
        _channels = channelWeights.Count;
        _enableTruePeak = enableTruePeak;
        _channelWeights = new double[_channels];
        for (var channel = 0; channel < _channels; channel++)
        {
            var weight = channelWeights[channel];
            if (!double.IsFinite(weight) || weight < 0)
            {
                throw new ArgumentException("声道权重必须是非负有限数。", nameof(channelWeights));
            }

            _channelWeights[channel] = weight;
        }

        // hop = (fs + 5) / 10：100 ms 四舍五入到样本，block = 4×hop（与 libebur128 / ffmpeg 对齐）。
        _hopSamples = (sampleRate + 5) / 10;
        _blockSamples = BlockHopCount * _hopSamples;
        _shortTermSamples = ShortTermHopCount * _hopSamples;

        var coefficients = DesignKWeighting(sampleRate);
        _preFilter = coefficients.PreFilter;
        _rlbFilter = coefficients.RlbFilter;
        _preStates = new FilterState[_channels];
        _rlbStates = new FilterState[_channels];
        _hopSums = new double[_channels];
        _hopHistory = new double[ShortTermHopCount][];
        for (var hop = 0; hop < ShortTermHopCount; hop++)
        {
            _hopHistory[hop] = new double[_channels];
        }

        _truePeakHistory = new double[_channels][];
        for (var channel = 0; channel < _channels; channel++)
        {
            _truePeakHistory[channel] = new double[TruePeakTaps];
        }
    }

    /// <summary>已处理的帧数（样本数 / 声道数）。</summary>
    public long ProcessedFrames => _processedFrames;

    /// <summary>
    /// 按 BS.1770-5 / libebur128 的解析设计公式生成任意采样率的 K 加权系数。
    /// 48 kHz 代入后与 BS.1770-5 Table 1/2 的官方系数逐位一致（差异 &lt; 1e-12），
    /// 因此不需要维护多套常数表；公开此方法便于审计与单元测试直接核对系数。
    /// </summary>
    /// <param name="sampleRate">采样率（Hz），须大于 0 且不低于 8000。</param>
    /// <returns>两级 biquad 系数。</returns>
    public static KWeightingCoefficients DesignKWeighting(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (sampleRate < 8000)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "采样率低于 8000 Hz，不是有效音频。");
        }

        // 第一级 pre-filter：模拟头部声学效应（参数取自 libebur128 ebur128_init_filter）。
        const double preFrequency = 1681.974450955533;
        const double preGainDb = 3.999843853973347;
        const double preQ = 0.7071752369554196;

        var k = Math.Tan(Math.PI * preFrequency / sampleRate);
        var vh = Math.Pow(10.0, preGainDb / 20.0);
        var vb = Math.Pow(vh, 0.4996667741545416);
        var a0 = 1.0 + k / preQ + k * k;
        var preFilter = new BiquadCoefficients(
            (vh + vb * k / preQ + k * k) / a0,
            2.0 * (k * k - vh) / a0,
            (vh - vb * k / preQ + k * k) / a0,
            2.0 * (k * k - 1.0) / a0,
            (1.0 - k / preQ + k * k) / a0);

        // 第二级 RLB 高通（-38 Hz 附近的高通，抵消 DC 与低频）。
        const double rlbFrequency = 38.13547087602444;
        const double rlbQ = 0.5003270373238773;

        var rlbK = Math.Tan(Math.PI * rlbFrequency / sampleRate);
        var d = 1.0 + rlbK / rlbQ + rlbK * rlbK;
        var rlbFilter = new BiquadCoefficients(
            1.0,
            -2.0,
            1.0,
            2.0 * (rlbK * rlbK - 1.0) / d,
            (1.0 - rlbK / rlbQ + rlbK * rlbK) / d);

        return new KWeightingCoefficients(preFilter, rlbFilter);
    }

    /// <summary>
    /// 喂入一段交错 float32 采样并完成本段内的 K 加权累积、分块与真峰值更新。
    /// </summary>
    /// <param name="interleavedSamples">
    /// 交错采样（帧内按声道顺序排列）。缓冲区长度必须不少于 <paramref name="frameCount"/> × 声道数。
    /// </param>
    /// <param name="frameCount">本段包含的帧数（每帧每声道一个样本）。</param>
    public void Process(ReadOnlySpan<float> interleavedSamples, int frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        if (interleavedSamples.Length < frameCount * _channels)
        {
            throw new ArgumentException(
                $"交错采样数不足：需要 {frameCount * _channels}，实际 {interleavedSamples.Length}。",
                nameof(interleavedSamples));
        }

        var offset = 0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            for (var channel = 0; channel < _channels; channel++)
            {
                var sample = (double)interleavedSamples[offset++];
                var magnitude = Math.Abs(sample);
                if (magnitude > _samplePeak)
                {
                    _samplePeak = magnitude;
                }

                if (_enableTruePeak)
                {
                    UpdateTruePeak(channel, sample);
                }

                // 两级 biquad 级联（Direct Form II Transposed）：每声道独立状态、跨块连续。
                ref var pre = ref _preStates[channel];
                var stage1 = _preFilter.B0 * sample + pre.Z1;
                pre.Z1 = _preFilter.B1 * sample - _preFilter.A1 * stage1 + pre.Z2;
                pre.Z2 = _preFilter.B2 * sample - _preFilter.A2 * stage1;

                ref var rlb = ref _rlbStates[channel];
                var stage2 = _rlbFilter.B0 * stage1 + rlb.Z1;
                rlb.Z1 = _rlbFilter.B1 * stage1 - _rlbFilter.A1 * stage2 + rlb.Z2;
                rlb.Z2 = _rlbFilter.B2 * stage1 - _rlbFilter.A2 * stage2;

                _hopSums[channel] += stage2 * stage2;
            }

            if (_enableTruePeak)
            {
                _truePeakWriteIndex++;
                if (_truePeakWriteIndex == TruePeakTaps)
                {
                    _truePeakWriteIndex = 0;
                }
            }

            _hopFill++;
            if (_hopFill >= _hopSamples)
            {
                FlushHop();
            }
        }

        _processedFrames += frameCount;
    }

    /// <summary>
    /// 结束测量并汇总结果。可在喂完全部采样后调用一次；重复调用返回相同结果。
    /// 短于 400 ms 或全静音时积分响度为 null（详见 <see cref="LoudnessMeasurement.Note"/>），不抛异常。
    /// </summary>
    /// <param name="duration">文件时长（来自解码器；只用于结果展示，不参与算法）。</param>
    /// <returns>测量结果。</returns>
    public LoudnessMeasurement Complete(TimeSpan duration)
    {
        var (integratedLufs, note) = ComputeIntegratedLufs();
        var loudnessRange = ComputeLoudnessRange();
        var samplePeakDb = ToDb(_samplePeak);
        var truePeakDb = _enableTruePeak ? ToDb(Math.Max(_truePeak, _samplePeak)) : samplePeakDb;

        return new LoudnessMeasurement(
            integratedLufs,
            loudnessRange,
            truePeakDb,
            samplePeakDb,
            _maxMomentaryLufs,
            _maxShortTermLufs,
            _sampleRate,
            _channels,
            duration,
            note);
    }

    /// <summary>累积满一个 hop 后：写入 hop 历史、更新 400 ms 块与 3 s 短时窗。</summary>
    private void FlushHop()
    {
        var slot = _hopWriteIndex;
        var hop = _hopHistory[slot];
        for (var channel = 0; channel < _channels; channel++)
        {
            hop[channel] = _hopSums[channel];
            _hopSums[channel] = 0.0;
        }

        _hopWriteIndex = slot + 1 == ShortTermHopCount ? 0 : slot + 1;
        _hopFill = 0;
        _completedHops++;

        FlushDenormals();
        UpdateMomentaryBlock();
        UpdateShortTerm();
    }

    /// <summary>更新 400 ms 门限块（也是 Momentary 值）：每 hop 一个，75% 重叠。</summary>
    private void UpdateMomentaryBlock()
    {
        if (_completedHops < BlockHopCount)
        {
            return;
        }

        var weightedSum = 0.0;
        for (var channel = 0; channel < _channels; channel++)
        {
            var hopSum = 0.0;
            for (var hop = 0; hop < BlockHopCount; hop++)
            {
                hopSum += NewestHop(hop)[channel];
            }

            weightedSum += _channelWeights[channel] * hopSum;
        }

        // 加权块能量 Σ G_i·z_ij（z 是块内均方）；门限与最终响度都基于它计算。
        var blockEnergy = weightedSum / _blockSamples;
        _blockEnergies.Add(blockEnergy);

        var momentaryLufs = ToLufs(blockEnergy);
        if (_maxMomentaryLufs is null || momentaryLufs > _maxMomentaryLufs)
        {
            _maxMomentaryLufs = momentaryLufs;
        }
    }

    /// <summary>更新 3 s 短时窗：100 ms 步进，不门限地记录最大值，并把 ≥ -70 LUFS 的值留给 LRA。</summary>
    private void UpdateShortTerm()
    {
        if (_completedHops < ShortTermHopCount)
        {
            return;
        }

        var weightedSum = 0.0;
        for (var channel = 0; channel < _channels; channel++)
        {
            var hopSum = 0.0;
            for (var hop = 0; hop < ShortTermHopCount; hop++)
            {
                hopSum += NewestHop(hop)[channel];
            }

            weightedSum += _channelWeights[channel] * hopSum;
        }

        var shortTermLufs = ToLufs(weightedSum / _shortTermSamples);
        if (_maxShortTermLufs is null || shortTermLufs > _maxShortTermLufs)
        {
            _maxShortTermLufs = shortTermLufs;
        }

        // LRA 绝对门限：只保留 ≥ -70 LUFS 的短时值；相对门限在 Complete 时统一应用。
        if (shortTermLufs >= AbsoluteGateLufs)
        {
            _shortTermLoudness.Add(shortTermLufs);
        }
    }

    /// <summary>
    /// 两遍门限求积分响度（BS.1770-5 eq.5–7）：
    /// 先按 -70 LUFS 绝对门限聚合平均能量算相对门限（−10 LU），再按相对门限聚合求最终 I。
    /// </summary>
    private (double? IntegratedLufs, string? Note) ComputeIntegratedLufs()
    {
        if (_blockEnergies.Count == 0)
        {
            return (null, _processedFrames == 0
                ? "没有可分析的采样"
                : "时长不足 400 ms，无法测量积分响度");
        }

        var absoluteGatedSum = 0.0;
        var absoluteGatedCount = 0;
        foreach (var blockEnergy in _blockEnergies)
        {
            if (ToLufs(blockEnergy) > AbsoluteGateLufs)
            {
                absoluteGatedSum += blockEnergy;
                absoluteGatedCount++;
            }
        }

        if (absoluteGatedCount == 0)
        {
            return (null, "全曲静音或低于 -70 LUFS，无法测量积分响度");
        }

        // 相对门限 = 绝对门限后的响度 − 10 LU；先在能量域平均，再算响度。
        var relativeThreshold = ToLufs(absoluteGatedSum / absoluteGatedCount) + IntegratedRelativeGateLu;

        var gatedSum = 0.0;
        var gatedCount = 0;
        foreach (var blockEnergy in _blockEnergies)
        {
            var blockLufs = ToLufs(blockEnergy);
            if (blockLufs > relativeThreshold && blockLufs > AbsoluteGateLufs)
            {
                gatedSum += blockEnergy;
                gatedCount++;
            }
        }

        if (gatedCount == 0)
        {
            return (null, "全曲静音或低于 -70 LUFS，无法测量积分响度");
        }

        return (ToLufs(gatedSum / gatedCount), null);
    }

    /// <summary>
    /// LRA（EBU Tech 3342）：绝对门限已在采集时应用；这里做 -20 LU 相对门限、
    /// 升序排序后取 P10/P95 最近秩（不插值），LRA = P95 − P10。
    /// </summary>
    private double? ComputeLoudnessRange()
    {
        if (_shortTermLoudness.Count == 0)
        {
            return null;
        }

        // 相对门限：S 已含 -0.691 偏移，这里不再减偏移。
        var meanPower = 0.0;
        foreach (var shortTermLufs in _shortTermLoudness)
        {
            meanPower += Math.Pow(10.0, shortTermLufs / 10.0);
        }

        meanPower /= _shortTermLoudness.Count;
        var relativeThreshold = 10.0 * Math.Log10(meanPower) + LoudnessRangeRelativeGateLu;

        var gated = new List<double>(_shortTermLoudness.Count);
        foreach (var shortTermLufs in _shortTermLoudness)
        {
            if (shortTermLufs >= relativeThreshold)
            {
                gated.Add(shortTermLufs);
            }
        }

        if (gated.Count == 0)
        {
            return null;
        }

        var sorted = gated.ToArray();
        Array.Sort(sorted);
        var lowIndex = (int)Math.Floor((sorted.Length - 1) * 0.10 + 0.5);
        var highIndex = (int)Math.Floor((sorted.Length - 1) * 0.95 + 0.5);
        return sorted[highIndex] - sorted[lowIndex];
    }

    /// <summary>取最近第 <paramref name="offset"/> 个已完成 hop 的环形缓冲槽（0 = 最新）。</summary>
    private double[] NewestHop(int offset)
    {
        var index = _hopWriteIndex - 1 - offset;
        if (index < 0)
        {
            index += ShortTermHopCount;
        }

        return _hopHistory[index];
    }

    /// <summary>真峰值：把原始 PCM 采样推入 12 抽头历史，按 ITU 4 相各算 12-tap FIR 并取绝对值最大。</summary>
    private void UpdateTruePeak(int channel, double sample)
    {
        var history = _truePeakHistory[channel];
        var index = _truePeakWriteIndex;
        history[index] = sample;

        var phase0 = 0.0;
        var phase1 = 0.0;
        var phase2 = 0.0;
        var phase3 = 0.0;
        for (var tap = 0; tap < TruePeakTaps; tap++)
        {
            var value = history[index];
            if (--index < 0)
            {
                index = TruePeakTaps - 1;
            }

            phase0 += TruePeakPhase0[tap] * value;
            phase1 += TruePeakPhase1[tap] * value;
            phase2 += TruePeakPhase2[tap] * value;
            phase3 += TruePeakPhase3[tap] * value;
        }

        var magnitude = Math.Max(
            Math.Max(Math.Abs(phase0), Math.Abs(phase1)),
            Math.Max(Math.Abs(phase2), Math.Abs(phase3)));
        if (magnitude > _truePeak)
        {
            _truePeak = magnitude;
        }
    }

    /// <summary>把过小的 biquad 状态清零，避免连续静音进入 double denormal 区间拖慢运算。</summary>
    private void FlushDenormals()
    {
        for (var channel = 0; channel < _channels; channel++)
        {
            ref var pre = ref _preStates[channel];
            if (Math.Abs(pre.Z1) < DenormalThreshold)
            {
                pre.Z1 = 0.0;
            }

            if (Math.Abs(pre.Z2) < DenormalThreshold)
            {
                pre.Z2 = 0.0;
            }

            ref var rlb = ref _rlbStates[channel];
            if (Math.Abs(rlb.Z1) < DenormalThreshold)
            {
                rlb.Z1 = 0.0;
            }

            if (Math.Abs(rlb.Z2) < DenormalThreshold)
            {
                rlb.Z2 = 0.0;
            }
        }
    }

    /// <summary>加权均方（Σ G_i·z_i）转响度：能量为 0 时返回负无穷（静音）。</summary>
    private static double ToLufs(double weightedMeanSquare) =>
        weightedMeanSquare > 0.0
            ? LoudnessOffset + 10.0 * Math.Log10(weightedMeanSquare)
            : double.NegativeInfinity;

    /// <summary>线性幅度转 dBFS：0（静音）返回负无穷。</summary>
    private static double ToDb(double amplitude) =>
        amplitude > 0.0 ? 20.0 * Math.Log10(amplitude) : double.NegativeInfinity;

    /// <summary>单个 biquad 的 Direct Form II Transposed 状态。</summary>
    private struct FilterState
    {
        public double Z1;
        public double Z2;
    }
}
