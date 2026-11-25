using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class BpmCoreTests
{
    // =====================================================================
    // BpmFormatter & BpmSettings
    // =====================================================================

    [Theory]
    [InlineData("128", 128.0)]
    [InlineData("128.5", 128.5)]
    [InlineData("  92.45 BPM ", 92.45)]
    [InlineData("20", 20.0)]
    [InlineData("300.0", 300.0)]
    public void BpmFormatter_TryParse_AcceptsValidNumbers(string input, double expected)
    {
        Assert.True(BpmFormatter.TryParse(input, out var parsed));
        Assert.Equal(expected, parsed, precision: 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("19.9")]
    [InlineData("300.1")]
    [InlineData("-120")]
    public void BpmFormatter_TryParse_RejectsOutOfRangeOrInvalid(string? input)
    {
        Assert.False(BpmFormatter.TryParse(input, out _));
    }

    [Fact]
    public void BpmFormatter_FormatDisplay_RespectsDecimalPlaces()
    {
        Assert.Equal("128.0", BpmFormatter.FormatDisplay(128.0, decimalPlaces: 1));
        Assert.Equal("127.5", BpmFormatter.FormatDisplay(127.54, decimalPlaces: 1));
        Assert.Equal("128", BpmFormatter.FormatDisplay(127.54, decimalPlaces: 0));
        Assert.Equal("—", BpmFormatter.FormatDisplay(null, decimalPlaces: 1));
    }

    [Fact]
    public void BpmFormatter_FormatTagText_WritesCleanIntegerOrOneDecimal()
    {
        Assert.Equal("128", BpmFormatter.FormatTagText(128.0));
        Assert.Equal("128.5", BpmFormatter.FormatTagText(128.5));
        Assert.Equal("129", BpmFormatter.FormatTagText(128.5, roundToInteger: true));
    }

    [Fact]
    public void BpmFormatter_HalveAndDouble_StayWithinBounds()
    {
        Assert.True(BpmFormatter.TryHalve(140.0, out var halved));
        Assert.Equal(70.0, halved, precision: 1);

        Assert.False(BpmFormatter.TryHalve(30.0, out _)); // 15 < 20

        Assert.True(BpmFormatter.TryDouble(92.5, out var doubled));
        Assert.Equal(185.0, doubled, precision: 1);

        Assert.False(BpmFormatter.TryDouble(160.0, out _)); // 320 > 300
    }

    [Theory]
    [InlineData("track.m4a", true)]
    [InlineData("track.MP4", true)]
    [InlineData("track.m4b", true)]
    [InlineData("track.flac", false)]
    [InlineData("track.mp3", false)]
    [InlineData("track.wav", false)]
    public void BpmFormatter_RequiresIntegerTag_MatchesAppleContainers(string path, bool expected)
    {
        Assert.Equal(expected, BpmFormatter.RequiresIntegerTag(path));
    }

    [Fact]
    public void BpmSettings_Normalize_ClampsInvalidValues()
    {
        var settings = BpmSettings.Normalize(new BpmSettings
        {
            PreferredModel = (BpmModelKind)99,
            DecimalPlaces = 5,
            IdleUnloadSeconds = 999
        });

        Assert.Equal(BpmModelKind.Small0, settings.PreferredModel);
        Assert.Equal(1, settings.DecimalPlaces);
        Assert.Equal(300, settings.IdleUnloadSeconds);

        var immediate = BpmSettings.Normalize(new BpmSettings
        {
            DecimalPlaces = -2,
            IdleUnloadSeconds = -5
        });
        Assert.Equal(0, immediate.DecimalPlaces);
        Assert.Equal(0, immediate.IdleUnloadSeconds);
    }

    // =====================================================================
    // MelSpectrogramMath
    // =====================================================================

    [Fact]
    public void MelSpectrogramMath_SlaneyConversion_RoundTripsAcrossRange()
    {
        double[] freqs = [30.0, 200.0, 999.0, 1000.0, 2500.0, 6400.0, 11000.0];
        foreach (var hz in freqs)
        {
            var mel = MelSpectrogramMath.HzToMelSlaney(hz);
            var back = MelSpectrogramMath.MelToHzSlaney(mel);
            Assert.InRange(Math.Abs(back - hz), 0.0, 1e-6);
        }
    }

    [Fact]
    public void MelSpectrogramMath_ReflectIndex_MatchesPyTorchReflectSemantics()
    {
        // length = 5 -> indices [0, 1, 2, 3, 4]
        // left reflect: -1 -> 1, -2 -> 2, -4 -> 4
        // right reflect: 5 -> 3, 6 -> 2, 8 -> 0
        Assert.Equal(1, MelSpectrogramMath.ReflectIndex(-1, 5));
        Assert.Equal(2, MelSpectrogramMath.ReflectIndex(-2, 5));
        Assert.Equal(4, MelSpectrogramMath.ReflectIndex(-4, 5));
        Assert.Equal(3, MelSpectrogramMath.ReflectIndex(5, 5));
        Assert.Equal(2, MelSpectrogramMath.ReflectIndex(6, 5));
        Assert.Equal(0, MelSpectrogramMath.ReflectIndex(8, 5));
    }

    [Fact]
    public void MelSpectrogramMath_Compute_ProducesExpectedShapeAndEnergyPeak()
    {
        // 2 seconds of 1000 Hz sine at 22,050 Hz
        const int sampleCount = MelSpectrogramMath.SampleRate * 2;
        var samples = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            samples[i] = MathF.Sin(2f * MathF.PI * 1000f * i / MelSpectrogramMath.SampleRate);
        }

        var (mel, frames) = MelSpectrogramMath.Compute(samples);
        Assert.Equal(1 + sampleCount / MelSpectrogramMath.HopLength, frames);
        Assert.Equal(frames * MelSpectrogramMath.NumMels, mel.Length);

        // Middle frame should have finite, non-negative values and a strong energy peak around 1000 Hz.
        var midFrame = frames / 2;
        var maxVal = 0f;
        var maxBin = -1;
        for (var m = 0; m < MelSpectrogramMath.NumMels; m++)
        {
            var v = mel[midFrame * MelSpectrogramMath.NumMels + m];
            Assert.True(float.IsFinite(v) && v >= 0f);
            if (v > maxVal)
            {
                maxVal = v;
                maxBin = m;
            }
        }

        Assert.True(maxVal > 4.0f);
        // 1000 Hz is around mel bin ~38 in 128-bin Slaney 30–11000 Hz.
        Assert.InRange(maxBin, 30, 50);
    }

    // =====================================================================
    // BeatThisPostProcessor
    // =====================================================================

    [Theory]
    [InlineData(72.0)]
    [InlineData(92.5)]
    [InlineData(110.3)]
    [InlineData(120.0)]
    [InlineData(128.0)]
    [InlineData(134.7)]
    [InlineData(174.0)]
    public void BeatThisPostProcessor_RecoversSubFrameBpmAccurately(double trueBpm)
    {
        var logits = SynthesizeBeatLogits(durationSeconds: 30.0, trueBpm);
        var estimation = BeatThisPostProcessor.EstimateGlobalBpm(logits);

        Assert.True(estimation.IsReliable);
        Assert.False(estimation.IsVariableTempoSuspected);
        Assert.InRange(Math.Abs(estimation.Bpm - trueBpm), 0.0, 0.25);
        Assert.True(estimation.Confidence >= 0.85);
        Assert.NotEmpty(estimation.Candidates);
    }

    [Fact]
    public void BeatThisPostProcessor_FlagsVariableTempo()
    {
        // First 15 seconds at 100 BPM, next 15 seconds at 130 BPM
        var totalFrames = (int)(30.0 * BeatThisPostProcessor.FramesPerSecond);
        var logits = new float[totalFrames];
        Array.Fill(logits, -6.0f);

        AddBeatImpulses(logits, startSec: 0.5, endSec: 14.5, bpm: 100.0);
        AddBeatImpulses(logits, startSec: 15.0, endSec: 29.5, bpm: 130.0);

        var estimation = BeatThisPostProcessor.EstimateGlobalBpm(logits);

        Assert.True(estimation.IsReliable);
        Assert.True(estimation.IsVariableTempoSuspected);
        Assert.False(estimation.TooManyTempoChanges);
        Assert.Equal(2, estimation.Segments.Count);
        Assert.InRange(Math.Abs(estimation.Segments[0].Bpm - 100.0), 0.0, 0.5);
        Assert.InRange(Math.Abs(estimation.Segments[1].Bpm - 130.0), 0.0, 0.5);
        Assert.NotNull(estimation.Note);
    }

    [Fact]
    public void TempoSegmentAnalyzer_IgnoresSilentBreakdownAndSingleBeatDropout()
    {
        // 128 BPM steady track with a 6-second drumless breakdown in the middle -> must stay 1 steady segment
        var totalFrames = (int)(44.0 * BeatThisPostProcessor.FramesPerSecond);
        var logits = new float[totalFrames];
        Array.Fill(logits, -6.0f);

        AddBeatImpulses(logits, startSec: 0.4, endSec: 18.0, bpm: 128.0);
        // 6-second silent breakdown (18.0 .. 24.0)
        AddBeatImpulses(logits, startSec: 24.0, endSec: 43.2, bpm: 128.0);

        var estimation = BeatThisPostProcessor.EstimateGlobalBpm(logits);
        Assert.True(estimation.IsReliable);
        Assert.False(estimation.IsVariableTempoSuspected);
        Assert.False(estimation.TooManyTempoChanges);
        Assert.Equal(1, estimation.DetectedSegmentCount);
        Assert.Empty(estimation.Segments);
        Assert.InRange(Math.Abs(estimation.Bpm - 128.0), 0.0, 0.3);
    }

    [Fact]
    public void TempoSegmentAnalyzer_DetectsThreeAndFiveSegmentsAccurately()
    {
        // 3-segment song: 92 BPM (18s) -> 132 BPM (30s, primary) -> 110 BPM (18s)
        var logits3 = SynthesizeMultiTempoLogits([
            (0.4, 18.0, 92.0),
            (18.2, 48.0, 132.0),
            (48.2, 66.0, 110.0)
        ], totalSeconds: 66.5);

        var est3 = BeatThisPostProcessor.EstimateGlobalBpm(logits3);
        Assert.True(est3.IsReliable);
        Assert.True(est3.IsVariableTempoSuspected);
        Assert.False(est3.TooManyTempoChanges);
        Assert.Equal(3, est3.Segments.Count);
        Assert.InRange(Math.Abs(est3.Segments[0].Bpm - 92.0), 0.0, 0.6);
        Assert.InRange(Math.Abs(est3.Segments[1].Bpm - 132.0), 0.0, 0.6);
        Assert.InRange(Math.Abs(est3.Segments[2].Bpm - 110.0), 0.0, 0.6);
        Assert.True(est3.Segments[1].IsPrimary);
        Assert.Equal(0.0, est3.Segments[0].StartSeconds, precision: 1);

        // 5-segment song (exact max display boundary = 5)
        var logits5 = SynthesizeMultiTempoLogits([
            (0.4, 15.0, 88.0),
            (15.2, 30.0, 116.0),
            (30.2, 45.0, 142.0),
            (45.2, 60.0, 102.0),
            (60.2, 75.0, 128.0)
        ], totalSeconds: 75.5);

        var est5 = BeatThisPostProcessor.EstimateGlobalBpm(logits5);
        Assert.True(est5.IsReliable);
        Assert.True(est5.IsVariableTempoSuspected);
        Assert.False(est5.TooManyTempoChanges);
        Assert.Equal(5, est5.Segments.Count);
        Assert.Equal(5, est5.DetectedSegmentCount);
    }

    [Fact]
    public void TempoSegmentAnalyzer_HidesSegmentsWhenExceedingFiveSegments()
    {
        // 6 distinct segments (> MaxDisplaySegments = 5) -> TooManyTempoChanges = true, Segments = empty
        var logits6 = SynthesizeMultiTempoLogits([
            (0.4, 14.0, 84.0),
            (14.2, 28.0, 112.0),
            (28.2, 42.0, 140.0),
            (42.2, 56.0, 96.0),
            (56.2, 70.0, 128.0),
            (70.2, 84.0, 156.0)
        ], totalSeconds: 84.5);

        var est6 = BeatThisPostProcessor.EstimateGlobalBpm(logits6);
        Assert.True(est6.IsReliable);
        Assert.True(est6.IsVariableTempoSuspected);
        Assert.True(est6.TooManyTempoChanges);
        Assert.True(est6.DetectedSegmentCount > BpmFormatter.MaxDisplaySegments);
        Assert.Empty(est6.Segments);
        Assert.Contains("5", est6.Note);
    }

    [Fact]
    public void BpmFormatter_TimeRangeAndRelativeRelation_FormatCleanly()
    {
        Assert.Equal("00:00 – 00:48", BpmFormatter.FormatTimeRange(0, 48));
        Assert.Equal("01:05 – 02:30", BpmFormatter.FormatTimeRange(65, 150));
        Assert.Equal("1:01:05", BpmFormatter.FormatTimestamp(3665));

        Assert.Equal("倍速", BpmFormatter.FormatRelativeTempoRelation(140.0, 70.0));
        Assert.Equal("半速", BpmFormatter.FormatRelativeTempoRelation(64.0, 128.0));
        Assert.Equal(string.Empty, BpmFormatter.FormatRelativeTempoRelation(110.0, 128.0));
    }

    [Fact]
    public void BeatThisPostProcessor_SilentOrShortLogits_ReturnUnreliable()
    {
        var silent = new float[500];
        Array.Fill(silent, -5.0f);
        Assert.False(BeatThisPostProcessor.EstimateGlobalBpm(silent).IsReliable);

        var tooShort = new float[5];
        Assert.False(BeatThisPostProcessor.EstimateGlobalBpm(tooShort).IsReliable);
    }

    // =====================================================================
    // TapTempoCalculator
    // =====================================================================

    [Fact]
    public void TapTempoCalculator_RecoversExactAndJitteredBpmViaOls()
    {
        var calc = new TapTempoCalculator();
        var period = 60.0 / 128.5; // ~0.46693 s
        double[] jitter = [0.0, +0.012, -0.008, +0.005, -0.010, +0.004, -0.003, +0.006];

        TapTempoSnapshot last = default;
        for (var i = 0; i < jitter.Length; i++)
        {
            last = calc.Tap(10.0 + i * period + jitter[i]);
        }

        Assert.True(last.IsStable);
        Assert.Equal(jitter.Length, last.TapCount);
        Assert.NotNull(last.Bpm);
        Assert.InRange(Math.Abs(last.Bpm!.Value - 128.5), 0.0, 0.5);
    }

    [Fact]
    public void TapTempoCalculator_DebouncesDoubleTapAndResetsOnTimeoutOrOutlier()
    {
        var calc = new TapTempoCalculator();
        var t = 1.0;

        Assert.Null(calc.Tap(t).Bpm);
        // 100 ms < 180 ms debounce -> ignored
        var debounced = calc.Tap(t + 0.10);
        Assert.Equal(1, debounced.TapCount);
        Assert.Null(debounced.Bpm);

        // Regular 120 BPM taps (0.5 s)
        for (var i = 1; i <= 5; i++)
        {
            calc.Tap(t + i * 0.5);
        }

        Assert.Equal(6, calc.TapCount);

        // Missed beat (+1.0 s = 2x interval, > 35% outlier) -> resets window to 2 taps
        var outlier = calc.Tap(t + 5 * 0.5 + 1.0);
        Assert.True(outlier.WasReset);
        Assert.Equal(2, outlier.TapCount);

        // Long pause (> 2.5 s) -> resets to 1 tap
        var timedOut = calc.Tap(t + 20.0);
        Assert.True(timedOut.WasReset);
        Assert.Equal(1, timedOut.TapCount);
        Assert.Null(timedOut.Bpm);
    }

    [Fact]
    public void TapTempoCalculator_CapsWindowAtMaxWindowSize()
    {
        var calc = new TapTempoCalculator();
        TapTempoSnapshot snap = default;
        for (var i = 0; i < 25; i++)
        {
            snap = calc.Tap(i * 0.5);
        }

        Assert.Equal(TapTempoCalculator.MaxWindowSize, snap.TapCount);
        Assert.Equal(120.0, snap.Bpm);
    }

    // =====================================================================
    // BpmResolver
    // =====================================================================

    [Fact]
    public void BpmResolver_PriorityAndSmartInvalidation()
    {
        var t0 = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddMinutes(5);

        // 1. CachedMeasured only
        var cache = new BpmCacheEntry
        {
            Path = "demo.flac",
            FileSize = 1000,
            ModifiedAtUtc = t0,
            DurationMs = 180_000,
            MeasuredBpm = 128.0,
            Confidence = 0.92,
            AnalyzerId = "beat_this_small0"
        };
        var r1 = BpmResolver.Resolve(fileTagBpm: null, cache, 1000, t0, 180_000);
        Assert.Equal(BpmSource.CachedMeasured, r1.DisplayState.Source);
        Assert.Equal(128.0, r1.DisplayState.EffectiveBpm);
        Assert.False(r1.ShouldRefreshStamp);

        // 2. Cover/title edit changes FileSize & MTime, but |ΔDuration| <= 1000 ms -> preserves cache!
        var r2 = BpmResolver.Resolve(fileTagBpm: null, cache, 50_000, t1, 180_200);
        Assert.Equal(BpmSource.CachedMeasured, r2.DisplayState.Source);
        Assert.Equal(128.0, r2.DisplayState.EffectiveBpm);
        Assert.True(r2.ShouldRefreshStamp);

        // 3. File replaced with different audio (|ΔDuration| > 1000 ms) -> invalidates measured
        var r3 = BpmResolver.Resolve(fileTagBpm: null, cache, 20_000, t1, 210_000);
        Assert.Equal(BpmSource.None, r3.DisplayState.Source);
        Assert.Null(r3.DisplayState.EffectiveBpm);

        // 4. ManualBpm wins over FileTag & Measured when file wasn't externally modified
        cache.ManualBpm = 126.5;
        var r4 = BpmResolver.Resolve(fileTagBpm: 128.0, cache, 1000, t0, 180_000);
        Assert.Equal(BpmSource.CachedManual, r4.DisplayState.Source);
        Assert.Equal(126.5, r4.DisplayState.EffectiveBpm);

        // 5. External tool edits file and writes a different BPM -> FileTag wins & sets hint
        var r5 = BpmResolver.Resolve(fileTagBpm: 130.0, cache, 1024, t1, 180_000);
        Assert.Equal(BpmSource.FileTag, r5.DisplayState.Source);
        Assert.Equal(130.0, r5.DisplayState.EffectiveBpm);
        Assert.True(r5.DisplayState.ExternalFileTagChangedHint);

        // 6. Audio duration changed while ManualBpm exists -> ManualBpm kept with warning
        var r6 = BpmResolver.Resolve(fileTagBpm: null, cache, 20_000, t1, 240_000);
        Assert.Equal(BpmSource.CachedManual, r6.DisplayState.Source);
        Assert.Equal(126.5, r6.DisplayState.EffectiveBpm);
        Assert.True(r6.DisplayState.DurationChangedWarning);
        Assert.Null(r6.DisplayState.MeasuredBpm);
    }

    // =====================================================================
    // TagBatchEditor BPM support
    // =====================================================================

    [Fact]
    public void TagBatchEditor_HandlesBpmFieldAndPreservesWhenUnchanged()
    {
        var source = new AudioTagData(
            "Demo Track 01", "Demo Artist", "Demo Album A", "Demo Artist", "", "", "",
            2026, 1, 1, 180_000, 320, 44100, 2, "FLAC", null, null)
        {
            Bpm = 124.5
        };

        var values = TagBatchEditor.GetFieldValues(source);
        Assert.Equal("124.5", values[TagFieldKeys.Bpm]);

        // Unchanged BPM -> BpmChanged = false
        var noBpmEdit = new TagBatchEdit();
        noBpmEdit.Fields[TagFieldKeys.Title] = "Demo Track 02";
        var model1 = TagBatchEditor.CreateEdit(source, noBpmEdit);
        Assert.False(model1.BpmChanged);
        Assert.Equal(124.5, model1.Bpm);

        // Changed BPM -> BpmChanged = true
        var bpmEdit = new TagBatchEdit();
        bpmEdit.Fields[TagFieldKeys.Bpm] = "132.0";
        var model2 = TagBatchEditor.CreateEdit(source, bpmEdit);
        Assert.True(model2.BpmChanged);
        Assert.Equal(132.0, model2.Bpm);

        // Cleared BPM -> BpmChanged = true, Bpm = null
        var clearEdit = new TagBatchEdit();
        clearEdit.Fields[TagFieldKeys.Bpm] = "";
        var model3 = TagBatchEditor.CreateEdit(source, clearEdit);
        Assert.True(model3.BpmChanged);
        Assert.Null(model3.Bpm);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static float[] SynthesizeBeatLogits(double durationSeconds, double bpm)
    {
        var totalFrames = (int)(durationSeconds * BeatThisPostProcessor.FramesPerSecond);
        var logits = new float[totalFrames];
        Array.Fill(logits, -6.0f);
        AddBeatImpulses(logits, startSec: 0.35, endSec: durationSeconds - 0.35, bpm);
        return logits;
    }

    private static float[] SynthesizeMultiTempoLogits(
        IReadOnlyList<(double StartSec, double EndSec, double Bpm)> sections,
        double totalSeconds)
    {
        var totalFrames = (int)(totalSeconds * BeatThisPostProcessor.FramesPerSecond);
        var logits = new float[totalFrames];
        Array.Fill(logits, -6.0f);
        foreach (var (start, end, bpm) in sections)
        {
            AddBeatImpulses(logits, start, end, bpm);
        }

        return logits;
    }

    private static void AddBeatImpulses(float[] logits, double startSec, double endSec, double bpm)
    {
        var period = 60.0 / bpm;
        for (var t = startSec; t <= endSec; t += period)
        {
            var exactFrame = t * BeatThisPostProcessor.FramesPerSecond;
            var center = (int)Math.Round(exactFrame);
            for (var df = -3; df <= 3; df++)
            {
                var idx = center + df;
                if (idx < 0 || idx >= logits.Length)
                {
                    continue;
                }

                var dist = idx - exactFrame;
                var val = (float)(10.5 * Math.Exp(-0.5 * dist * dist / 0.6) - 6.0);
                if (val > logits[idx])
                {
                    logits[idx] = val;
                }
            }
        }
    }
}
