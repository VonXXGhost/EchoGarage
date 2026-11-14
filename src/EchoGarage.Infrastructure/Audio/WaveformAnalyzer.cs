using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using NAudio.Dsp;
using NAudio.Wave;

namespace EchoGarage.Infrastructure.Audio;

/// <summary>
/// ADR-015 §6 的波形分析器：AudioFileReader 解码 → 单声道下混 → STFT（2048 / Hann / 10 ms hop）
/// → 三频段 RMS + 总 RMS → 10 ms 包络 → 20 ms 一列 → 逐曲 dB 分位拉伸 → 高度与份额。
/// 纯计算：自己不 Task.Run（由 WaveformService 包在后台线程里），测试可以直接同步调用。
/// </summary>
public sealed class WaveformAnalyzer : IWaveformAnalyzer
{
    /// <summary>算法版本；EGWV 头里记录它，版本变化即触发重算。</summary>
    public const int AnalyzerVersion = 1;

    /// <summary>每次从解码器读取的交错样本数（ADR-015 §6.10 的 4096 块口径，取 4 倍以摊薄调用开销）。</summary>
    private const int ReadBlockFrames = 16384;

    private readonly int _publishChunkColumns;

    /// <param name="publishChunkColumns">
    /// 渐进发布步长（列）。默认 250 列 = 5 s 音频（ADR-015 §6.10）；测试可传更小的值以便观察分段行为，
    /// 它只影响发布节奏，不影响最终结果。
    /// </param>
    public WaveformAnalyzer(int publishChunkColumns = WaveformMath.PublishColumns)
    {
        _publishChunkColumns = Math.Max(1, publishChunkColumns);
    }

    public int Version => AnalyzerVersion;

    public async Task<WaveformPeaks> AnalyzeAsync(
        string filePath,
        IProgress<WaveformPeaks>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"音频文件不存在：{filePath}", filePath);
        }

        using var reader = new AudioFileReader(filePath);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = Math.Max(1, reader.WaveFormat.Channels);
        var hop = WaveformMath.HopSize(sampleRate);
        var duration = reader.TotalTime;

        // 总列数由时长推算（缓存与渐进快照的 ColumnCount 必须一开始就稳定）。
        var totalSamples = (long)Math.Round(duration.TotalSeconds * sampleRate);
        var frameCount = totalSamples < WaveformMath.FftSize
            ? 0
            : (int)((totalSamples - WaveformMath.FftSize) / hop) + 1;
        var columnCapacity = WaveformMath.ColumnCountForFrames(frameCount);

        if (columnCapacity == 0)
        {
            // 短于一个 FFT 窗口（46 ms）：没有可绘制的列，直接给出完成的空快照。
            var empty = WaveformPeaks.Empty(duration, sampleRate);
            progress?.Report(empty);
            return empty;
        }

        var state = new AnalysisState(sampleRate, columnCapacity, duration);
        var window = WaveformMath.CreateHannWindow();
        var samples = new double[WaveformMath.FftSize];
        var mono = new float[ReadBlockFrames];
        var interleaved = new float[ReadBlockFrames * channels];

        var buffered = 0;
        var framesDone = 0;
        var nextPublish = _publishChunkColumns;
        // 渐进快照一律用固定窗口（−48…0 dB）：上限锁在 0 dBFS，已发布的列不会因为后续内容更响
        // 而整体变矮 / 变高，波形在生成过程中只会向右延伸（owner 2026-11-14 反馈）。
        var (streamingFloorDb, streamingCeilingDb) = WaveformMath.StreamingWindow();

        while (framesDone < frameCount)
        {
            var read = reader.Read(interleaved, 0, interleaved.Length);
            if (read <= 0)
            {
                break;
            }

            var framesRead = read / channels;
            if (framesRead <= 0)
            {
                break;
            }

            for (var frame = 0; frame < framesRead; frame++)
            {
                if (channels == 1)
                {
                    mono[frame] = interleaved[frame];
                }
                else
                {
                    var sum = 0f;
                    var baseIndex = frame * channels;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += interleaved[baseIndex + channel];
                    }

                    mono[frame] = sum / channels;
                }
            }

            var offset = 0;
            var publishDue = false;
            while (offset < framesRead && framesDone < frameCount)
            {
                var take = Math.Min(WaveformMath.FftSize - buffered, framesRead - offset);
                Array.Copy(mono, offset, samples, buffered, take);
                buffered += take;
                offset += take;
                if (buffered < WaveformMath.FftSize)
                {
                    break;
                }

                state.AddFrame(samples, window);
                framesDone++;

                // 滑动窗口前进一个 hop。
                Array.Copy(samples, hop, samples, 0, WaveformMath.FftSize - hop);
                buffered = WaveformMath.FftSize - hop;

                if (framesDone % WaveformMath.CancelCheckFrames == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (state.Columns < nextPublish || state.Columns >= columnCapacity)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(state.CreateSnapshot(state.Columns, streamingFloorDb, streamingCeilingDb));
                nextPublish += _publishChunkColumns;
                publishDue = true;
            }

            if (publishDue)
            {
                // 每段结束让出线程，播放解码优先（ADR-015 §6.10 的调度要求）。
                await Task.Yield();
            }
        }

        // 奇数帧时最后一列只有一帧，仍需收尾。
        state.FlushPartialColumn();

        // 完成时用全曲 p5 / p99.5 重算所有高度并发布最终快照（owner 2026-11-14 的归一化策略：
        // 分析过程中锁死窗口、零重排，只在分析完成时做这一次整曲归一化）。这也是
        // “分块分析与整曲分析逐字节一致”的保证：最终结果不依赖此前发布过哪些前缀快照。
        var finalWindow = WaveformMath.StretchWindow(state.CollectDecibels(state.Columns));
        var final = state.CreateSnapshot(columnCapacity, finalWindow.FloorDb, finalWindow.CeilingDb);
        progress?.Report(final);
        return final;
    }

    /// <summary>一次分析的累加状态（只在分析线程上访问）。</summary>
    private sealed class AnalysisState
    {
        private readonly int _sampleRate;
        private readonly TimeSpan _duration;
        private readonly int _nyquist;
        private readonly int _lowFrom, _lowTo;
        private readonly int _midFrom, _midTo;
        private readonly int _highFrom, _highTo;
        private readonly double _parsevalDenominator;
        private readonly Complex[] _spectrum = new Complex[WaveformMath.FftSize];
        private readonly double[] _columnRms;
        private readonly byte[] _lowShare;
        private readonly byte[] _midShare;
        private readonly byte[] _highShare;

        private double _envTotal, _envLow, _envMid, _envHigh;
        private double _columnEnvelopeSum;
        private double _columnRawSquareSum;
        private double _columnLowSquareSum, _columnMidSquareSum, _columnHighSquareSum;
        private int _framesInColumn;

        public AnalysisState(int sampleRate, int capacity, TimeSpan duration)
        {
            _sampleRate = sampleRate;
            _duration = duration;
            _nyquist = WaveformMath.FftSize / 2;
            Capacity = capacity;
            _columnRms = new double[capacity];

            // 未填充的列（解码比 TotalTime 短时）按静音处理，份额是中性值，保证缓存里的
            // low+mid+high == 255 恒成立。
            _lowShare = new byte[capacity];
            _midShare = new byte[capacity];
            _highShare = new byte[capacity];
            Array.Fill(_lowShare, WaveformMath.SilentShare);
            Array.Fill(_midShare, WaveformMath.SilentShare);
            Array.Fill(_highShare, WaveformMath.SilentShare);

            _lowFrom = BinAtOrAbove(WaveformMath.LowBandMinHz);
            _lowTo = BinBelow(WaveformMath.LowCrossoverHz);
            _midFrom = _lowTo + 1;
            _midTo = BinBelow(WaveformMath.HighCrossoverHz);
            _highFrom = _midTo + 1;
            _highTo = Math.Min(_nyquist, (int)Math.Floor(WaveformMath.HighBandMaxHz * WaveformMath.FftSize / sampleRate));
            // NAudio 的正向 FFT 会把每个输出除以 N（归一化），因此 Rms 里的 P_b 已经是 ADR-015
            // §6.3 未归一化谱功率的 1/N²；MS_b = P_b/(N²·mean(w²)) 于是简化为 P_b/mean(w²)。
            _parsevalDenominator = WaveformMath.HannMeanSquare;
        }

        public int Capacity { get; }

        /// <summary>已完成（或收尾）的列数。</summary>
        public int Columns { get; private set; }

        /// <summary>处理一帧 10 ms 音频：加窗 → FFT → 三频段 RMS → 包络 → 攒列。</summary>
        public void AddFrame(ReadOnlySpan<double> samples, ReadOnlySpan<double> window)
        {
            for (var i = 0; i < WaveformMath.FftSize; i++)
            {
                _spectrum[i].X = (float)(samples[i] * window[i]);
                _spectrum[i].Y = 0f;
            }

            FastFourierTransform.FFT(true, 11, _spectrum);

            var lowRms = Rms(_lowFrom, _lowTo);
            var midRms = Rms(_midFrom, _midTo);
            var highRms = Rms(_highFrom, _highTo);
            // 总 RMS 按 ADR-015 §6.3 只合并三个频段（忽略 20 Hz 以下与 16 kHz 以上）。
            var totalRms = Math.Sqrt(lowRms * lowRms + midRms * midRms + highRms * highRms);

            _envTotal = WaveformMath.UpdateEnvelope(_envTotal, totalRms);
            _envLow = WaveformMath.UpdateEnvelope(_envLow, lowRms);
            _envMid = WaveformMath.UpdateEnvelope(_envMid, midRms);
            _envHigh = WaveformMath.UpdateEnvelope(_envHigh, highRms);

            _columnEnvelopeSum += _envTotal;
            _columnRawSquareSum += totalRms * totalRms;
            _columnLowSquareSum += _envLow * _envLow;
            _columnMidSquareSum += _envMid * _envMid;
            _columnHighSquareSum += _envHigh * _envHigh;
            _framesInColumn++;

            if (_framesInColumn >= WaveformMath.FramesPerColumn)
            {
                CompleteColumn();
            }
        }

        /// <summary>把不足两帧的最后一列也落下来（奇数帧文件）。</summary>
        public void FlushPartialColumn()
        {
            if (_framesInColumn > 0)
            {
                CompleteColumn();
            }
        }

        private void CompleteColumn()
        {
            var frames = _framesInColumn;
            var rawRms = Math.Sqrt(_columnRawSquareSum / frames);
            var envRms = _columnEnvelopeSum / frames;
            var lowRms = Math.Sqrt(_columnLowSquareSum / frames);
            var midRms = Math.Sqrt(_columnMidSquareSum / frames);
            var highRms = Math.Sqrt(_columnHighSquareSum / frames);

            // 索引越界保护：帧数推算比实际多时忽略多余列。
            if (Columns < Capacity)
            {
                // 静音列直接存 0：既得到高度 0，也让它不参与分位拉伸（否则长静音会把 p5 拉到 −100 dB）。
                _columnRms[Columns] = WaveformMath.IsSilent(rawRms) ? 0.0 : envRms;
                var shares = WaveformMath.IsSilent(rawRms)
                    ? (Low: WaveformMath.SilentShare, Mid: WaveformMath.SilentShare, High: WaveformMath.SilentShare)
                    : WaveformMath.QuantizeShares(lowRms, midRms, highRms);
                _lowShare[Columns] = shares.Low;
                _midShare[Columns] = shares.Mid;
                _highShare[Columns] = shares.High;
                Columns++;
            }

            _framesInColumn = 0;
            _columnEnvelopeSum = 0;
            _columnRawSquareSum = 0;
            _columnLowSquareSum = 0;
            _columnMidSquareSum = 0;
            _columnHighSquareSum = 0;
        }

        /// <summary>收集前 <paramref name="count"/> 列里非静音列的 dB（供分位拉伸）。</summary>
        public double[] CollectDecibels(int count) =>
            WaveformMath.CollectNonSilentDecibels(_columnRms.AsSpan(0, Math.Clamp(count, 0, Capacity)));

        /// <summary>构造一个不可变快照；数组按 <paramref name="validCount"/> 截断复制。</summary>
        public WaveformPeaks CreateSnapshot(int validCount, double floorDb, double ceilingDb)
        {
            var count = Math.Clamp(validCount, 0, Capacity);
            return new WaveformPeaks
            {
                ColumnsPerSecond = WaveformMath.ColumnsPerSecond,
                ColumnCount = Capacity,
                ValidCount = count,
                Duration = _duration,
                SampleRate = _sampleRate,
                FloorDb = floorDb,
                CeilingDb = ceilingDb,
                Height = WaveformMath.BuildHeights(_columnRms.AsSpan(0, count), floorDb, ceilingDb),
                Low = _lowShare.AsSpan(0, count).ToArray(),
                Mid = _midShare.AsSpan(0, count).ToArray(),
                High = _highShare.AsSpan(0, count).ToArray(),
            };
        }

        /// <summary>两个频段边界之间（闭区间）的 Parseval 折算 RMS。</summary>
        private double Rms(int from, int to)
        {
            if (from > to)
            {
                return 0.0;
            }

            double power = 0.0;
            for (var k = from; k <= to; k++)
            {
                var bin = _spectrum[k];
                var magnitude = (double)bin.X * bin.X + (double)bin.Y * bin.Y;
                // DC 与 Nyquist 不镜像，权重 1；其余 bin 的正负频率各占一半，权重 2。
                power += magnitude * (k == 0 || k == _nyquist ? 1.0 : 2.0);
            }

            return Math.Sqrt(power / _parsevalDenominator);
        }

        /// <summary>频率 ≥ <paramref name="hz"/> 的第一个 bin（至少为 1，DC 不参与频段）。</summary>
        private int BinAtOrAbove(double hz) =>
            Math.Clamp(
                (int)Math.Ceiling(hz * WaveformMath.FftSize / _sampleRate),
                1,
                _nyquist);

        /// <summary>频率严格小于 <paramref name="hz"/> 的最后一个 bin。</summary>
        private int BinBelow(double hz) =>
            Math.Clamp(
                (int)Math.Ceiling(hz * WaveformMath.FftSize / _sampleRate) - 1,
                0,
                _nyquist);
    }
}
