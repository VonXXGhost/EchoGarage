using EchoGarage.Core.Models;
using EchoGarage.Core.Services;
using EchoGarage.Infrastructure.Audio;

namespace EchoGarage.Tests;

/// <summary>
/// ADR-015 §6 / §6.10、ADR-015-A2 波形分析器：频段份额、静音、渐进发布、分块一致性、取消。
/// 分析器是纯计算（不 Task.Run），测试里直接 await。
/// </summary>
public sealed class WaveformAnalyzerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ---- 三频段份额（§6.3 / §6.7）----

    [Theory]
    [InlineData(100.0, "low")]
    [InlineData(1000.0, "mid")]
    [InlineData(8000.0, "high")]
    public async Task Analyze_SineWave_ReportsDominantBandShare(double frequency, string band)
    {
        var wav = _workspace.Combine($"sine-{band}.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 1.0, frequency: frequency);
        var analyzer = new WaveformAnalyzer();

        var peaks = await analyzer.AnalyzeAsync(wav);

        Assert.True(peaks.IsComplete);
        Assert.True(peaks.ColumnCount > 0);
        // 文件开头包络从 0 爬升（attack 60 ms），前几列会低于 p5 下限而为 0，属预期。
        Assert.All(peaks.Height.Skip(4), height => Assert.True(height > 0, "正弦稳定段不应被判为静音列"));
        var average = band switch
        {
            "low" => MeanShare(peaks.Low),
            "mid" => MeanShare(peaks.Mid),
            _ => MeanShare(peaks.High),
        };
        Assert.True(average > 0.9, $"{frequency} Hz 应主要落在 {band} 频段，实际份额均值 {average:F1}");
    }

    [Fact]
    public async Task Analyze_ConstantLevelSine_FinalSnapshotStaysVisible()
    {
        // ADR-015-A2：恒定电平 / 窄动态素材 p5 ≈ p99.5，旧窗口会把所有列压到 0 高度（空图）。
        // 新窗口把下界向下扩展到 MinimumStretchDb，稳定段必须接近满高。
        var wav = _workspace.Combine("constant-level.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 3.0, frequency: 440);
        var progress = new SyncProgress<WaveformPeaks>();
        var analyzer = new WaveformAnalyzer(publishChunkColumns: 100);

        var peaks = await analyzer.AnalyzeAsync(wav, progress);

        Assert.True(peaks.IsComplete);
        Assert.True(peaks.ColumnCount > 0);
        Assert.Equal(WaveformMath.MinimumStretchDb, peaks.CeilingDb - peaks.FloorDb, 6);
        Assert.True(peaks.FloorDb >= WaveformMath.SilenceDb);
        // 只有开头包络从 0 爬升的少数列可能偏低；稳定段应显示为高柱。
        Assert.All(peaks.Height.Skip(6), height => Assert.True(height > 200, $"恒定电平素材稳定段应有高度，实际 {height}"));
        // 渐进快照仍使用固定 −48…0 dB 窗口（ADR-015 §6.10 行为不变）。
        Assert.All(progress.Items.Where(item => !item.IsComplete), snapshot =>
        {
            Assert.Equal(WaveformMath.StreamingWindowFloorDb, snapshot.FloorDb);
            Assert.Equal(WaveformMath.StreamingWindowCeilingDb, snapshot.CeilingDb);
        });
    }

    [Fact]
    public async Task Analyze_DoubledAmplitude_RaisesHeightInFixedStreamingWindowSnapshot()
    {
        // 整曲归一化是振幅无关的（p5/p99.5 一起平移），振幅差异只能在固定窗口（−48…0 dB）的
        // 首个前缀快照里观察：每列的 dB 应整体 +6 dB → 高度单调增加。
        var quiet = WriteShaped("shaped-quiet.wav", scale: 0.5);
        var loud = WriteShaped("shaped-loud.wav", scale: 1.0);
        var analyzer = new WaveformAnalyzer(publishChunkColumns: 20);
        var quietProgress = new SyncProgress<WaveformPeaks>();
        var loudProgress = new SyncProgress<WaveformPeaks>();
        await analyzer.AnalyzeAsync(quiet, quietProgress);
        await analyzer.AnalyzeAsync(loud, loudProgress);

        var quietFirst = Assert.Single(quietProgress.Items.Take(1));
        var loudFirst = Assert.Single(loudProgress.Items.Take(1));
        Assert.Equal(quietFirst.ValidCount, loudFirst.ValidCount);
        Assert.True(quietFirst.ValidCount < quietFirst.ColumnCount, "首个前缀快照应是未完成快照");

        var quietSum = quietFirst.Height.Sum(height => (int)height);
        var loudSum = loudFirst.Height.Sum(height => (int)height);
        Assert.Equal(WaveformMath.StreamingWindowFloorDb, quietFirst.FloorDb);
        Assert.Equal(WaveformMath.StreamingWindowCeilingDb, quietFirst.CeilingDb);
        Assert.True(loudSum > quietSum, $"振幅翻倍后高度应增加：quiet={quietSum} loud={loudSum}");
        var meanDelta = (loudSum - quietSum) / (double)quietFirst.Height.Length;
        Assert.InRange(meanDelta, 20.0, 60.0); // 6 dB / 48 dB 量程 ≈ 32
    }

    // ---- 静音（§6.6）----

    [Fact]
    public async Task Analyze_SilentFile_ProducesZeroHeightsAndNeutralShares()
    {
        var wav = _workspace.Combine("silence.wav");
        WaveformTestAudio.WriteSilence(wav, seconds: 2.0);
        var analyzer = new WaveformAnalyzer();

        var peaks = await analyzer.AnalyzeAsync(wav);

        Assert.True(peaks.IsComplete);
        Assert.Equal(peaks.ColumnCount, peaks.ValidCount);
        Assert.All(peaks.Height, height => Assert.Equal(0, height));
        Assert.All(peaks.Low, share => Assert.Equal(WaveformMath.SilentShare, share));
        Assert.All(peaks.Mid, share => Assert.Equal(WaveformMath.SilentShare, share));
        Assert.All(peaks.High, share => Assert.Equal(WaveformMath.SilentShare, share));
    }

    // ---- 快照不变量 ----

    [Fact]
    public async Task Analyze_Snapshots_HaveValidPrefixArraysAndSums()
    {
        var wav = WriteShaped("invariants.wav", scale: 1.0);
        var progress = new SyncProgress<WaveformPeaks>();
        var analyzer = new WaveformAnalyzer(publishChunkColumns: 40);

        var final = await analyzer.AnalyzeAsync(wav, progress);

        Assert.True(progress.Items.Count > 1, "6 s 音频按 40 列分块应发布多次");
        foreach (var snapshot in progress.Items)
        {
            Assert.Equal(snapshot.ColumnCount, final.ColumnCount); // 容量从一开始就稳定
            Assert.InRange(snapshot.ValidCount, 0, snapshot.ColumnCount);
            Assert.Equal(snapshot.ValidCount, snapshot.Height.Length);
            Assert.Equal(snapshot.ValidCount, snapshot.Low.Length);
            Assert.Equal(snapshot.ValidCount, snapshot.Mid.Length);
            Assert.Equal(snapshot.ValidCount, snapshot.High.Length);
            Assert.Equal(snapshot.Duration, final.Duration);
            for (var i = 0; i < snapshot.ValidCount; i++)
            {
                Assert.Equal(255, snapshot.Low[i] + snapshot.Mid[i] + snapshot.High[i]);
            }
        }

        Assert.True(final.IsComplete);
        Assert.Equal(final.ColumnCount, final.ValidCount);
        Assert.Equal(final.ValidCount, final.Height.Length);
        Assert.False(progress.Items[0].IsComplete);
    }

    [Fact]
    public async Task Analyze_ProgressSnapshots_KeepFixedWindowUntilComplete()
    {
        // owner 2026-11-14：波形生成过程中不能“长高 / 缩矮”。渐进快照必须一直用固定窗口
        // （−48…0 dB，上限 0 dBFS），只有最终完成快照才允许换成整曲 p5 / p99.5。
        var wav = WriteShaped("stable-window.wav", seconds: 6.0, scale: 1.0);
        var progress = new SyncProgress<WaveformPeaks>();
        var analyzer = new WaveformAnalyzer(publishChunkColumns: 40);

        await analyzer.AnalyzeAsync(wav, progress);

        var streaming = progress.Items.Where(item => !item.IsComplete).ToList();
        Assert.True(streaming.Count > 1, "6 s 音频按 40 列分块应发布多次渐进快照");
        Assert.All(streaming, snapshot =>
        {
            Assert.Equal(WaveformMath.StreamingWindowFloorDb, snapshot.FloorDb);
            Assert.Equal(WaveformMath.StreamingWindowCeilingDb, snapshot.CeilingDb);
        });

        // 已画出的列在后续快照里高度必须完全一致：这就是“生成过程中不变形”的直接检验。
        for (var i = 1; i < streaming.Count; i++)
        {
            var previous = streaming[i - 1];
            var current = streaming[i];
            Assert.True(previous.ValidCount <= current.ValidCount);
            for (var column = 0; column < previous.ValidCount; column++)
            {
                Assert.Equal(previous.Height[column], current.Height[column]);
            }
        }
    }

    [Fact]
    public async Task Analyze_ProgressPrefixSnapshots_AreNotMutatedLater()
    {
        var wav = WriteShaped("immutable.wav", scale: 1.0);
        var progress = new SyncProgress<WaveformPeaks>();
        var analyzer = new WaveformAnalyzer(publishChunkColumns: 40);

        await analyzer.AnalyzeAsync(wav, progress);

        var first = progress.Items[0];
        var firstHeights = first.Height.ToArray();
        var firstLow = first.Low.ToArray();
        Assert.Equal(firstHeights, first.Height);
        Assert.Equal(firstLow, first.Low);
        Assert.NotSame(first.Height, progress.Items[^1].Height);
    }

    // ---- 分块一致性（§6.10）----

    [Fact]
    public async Task Analyze_ChunkedAndWholeFile_ProduceByteIdenticalResult()
    {
        var wav = WriteShaped("chunking.wav", seconds: 6.0, scale: 1.0);
        var whole = await new WaveformAnalyzer(publishChunkColumns: 250).AnalyzeAsync(wav);
        var chunkedProgress = new SyncProgress<WaveformPeaks>();
        var chunked = await new WaveformAnalyzer(publishChunkColumns: 37).AnalyzeAsync(wav, chunkedProgress);

        Assert.True(chunkedProgress.Items.Count > 2);
        Assert.Equal(whole.ColumnCount, chunked.ColumnCount);
        Assert.Equal(whole.ValidCount, chunked.ValidCount);
        Assert.Equal(whole.Duration, chunked.Duration);
        Assert.Equal((float)whole.FloorDb, (float)chunked.FloorDb);
        Assert.Equal((float)whole.CeilingDb, (float)chunked.CeilingDb);
        Assert.Equal(whole.Height, chunked.Height);
        Assert.Equal(whole.Low, chunked.Low);
        Assert.Equal(whole.Mid, chunked.Mid);
        Assert.Equal(whole.High, chunked.High);
    }

    [Fact]
    public async Task Analyze_SameFileTwice_IsDeterministic()
    {
        var wav = WriteShaped("deterministic.wav", seconds: 2.0, scale: 1.0);
        var analyzer = new WaveformAnalyzer();

        var first = await analyzer.AnalyzeAsync(wav);
        var second = await analyzer.AnalyzeAsync(wav);

        Assert.Equal(first.Height, second.Height);
        Assert.Equal(first.Low, second.Low);
        Assert.Equal(first.Mid, second.Mid);
        Assert.Equal(first.High, second.High);
    }

    // ---- 取消（§6.10）----

    [Fact]
    public async Task Analyze_CanceledDuringProgress_ThrowsAndStopsReporting()
    {
        var wav = WriteShaped("cancel.wav", seconds: 20.0, scale: 1.0);
        var progress = new SyncProgress<WaveformPeaks>();
        using var cts = new CancellationTokenSource();
        var countAtCancel = -1;
        progress.OnReport = _ =>
        {
            if (countAtCancel < 0)
            {
                countAtCancel = progress.Items.Count;
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WaveformAnalyzer(publishChunkColumns: 100).AnalyzeAsync(wav, progress, cts.Token));

        Assert.True(countAtCancel > 0);
        Assert.Equal(countAtCancel, progress.Items.Count); // 取消后不应再有回调
    }

    [Fact]
    public async Task Analyze_AlreadyCanceledToken_Throws()
    {
        var wav = _workspace.Combine("pre-canceled.wav");
        WaveformTestAudio.WriteSilence(wav, seconds: 0.2);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WaveformAnalyzer().AnalyzeAsync(wav, cancellationToken: cts.Token));
    }

    // ---- 边界情况 ----

    [Fact]
    public async Task Analyze_MissingFile_ThrowsFileNotFound()
    {
        var missing = _workspace.Combine("missing.wav");

        await Assert.ThrowsAsync<FileNotFoundException>(() => new WaveformAnalyzer().AnalyzeAsync(missing));
    }

    [Fact]
    public async Task Analyze_FileShorterThanFftWindow_ReturnsCompleteEmptySnapshot()
    {
        var wav = _workspace.Combine("tiny.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 0.02, frequency: 440);
        var progress = new SyncProgress<WaveformPeaks>();

        var peaks = await new WaveformAnalyzer().AnalyzeAsync(wav, progress);

        Assert.Equal(0, peaks.ColumnCount);
        Assert.Equal(0, peaks.ValidCount);
        Assert.True(peaks.IsComplete);
        Assert.Empty(peaks.Height);
        Assert.Single(progress.Items);
    }

    [Fact]
    public async Task Analyze_ReportsSampleRateAndDuration()
    {
        var wav = _workspace.Combine("metadata.wav");
        WaveformTestAudio.WriteSine(wav, seconds: 3.0, frequency: 440);

        var peaks = await new WaveformAnalyzer().AnalyzeAsync(wav);

        Assert.Equal(44100, peaks.SampleRate);
        Assert.InRange(peaks.Duration.TotalSeconds, 2.999, 3.001);
        Assert.Equal(WaveformMath.ColumnsPerSecond, peaks.ColumnsPerSecond);
        Assert.Equal(148, peaks.ColumnCount); // 3 s → 296 帧 → 148 列
    }

    // ---- 辅助 ----

    /// <summary>平均份额（0..255 换算成 0..1）。</summary>
    private static double MeanShare(byte[] shares) =>
        shares.Length == 0 ? 0.0 : shares.Average(share => share / 255.0);

    private string WriteShaped(string name, double seconds = 6.0, double scale = 1.0)
    {
        var path = _workspace.Combine(name);
        var sampleCount = (int)Math.Round(seconds * WaveformTestAudio.DefaultSampleRate);
        var sampleRate = (double)WaveformTestAudio.DefaultSampleRate;
        var fadeSamples = Math.Max(1, (int)(0.03 * sampleRate));
        return WaveformTestAudio.Write(
            path,
            seconds,
            i =>
            {
                var position = (double)i / sampleCount;
                var amplitude = scale * (0.12 + 0.5 * (0.5 + 0.5 * Math.Sin(2.0 * Math.PI * 0.8 * position)));
                var fade = Math.Min(1.0, Math.Min(i / (double)fadeSamples, (sampleCount - i) / (double)fadeSamples));
                return amplitude * fade * Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate);
            });
    }
}
