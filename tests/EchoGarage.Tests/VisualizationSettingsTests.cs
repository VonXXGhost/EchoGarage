using EchoGarage.Core.Models;
using EchoGarage.Infrastructure.Configuration;
using EchoGarage.Infrastructure.Logging;

namespace EchoGarage.Tests;

/// <summary>ADR-023 §5 的持久化参数：默认值、钳制、快照不可变、经 settings.json 往返。</summary>
public sealed class VisualizationSettingsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    [Fact]
    public void Defaults_MatchDocumentedPreset()
    {
        var settings = VisualizationSettings.Normalize(null);

        Assert.Equal(VisualizerKind.Spectrum, settings.Kind);
        Assert.Equal(32, settings.Spectrum.BandCount);
        Assert.Equal(4096, settings.Spectrum.FftSize);
        Assert.Equal(40, settings.Spectrum.MinFrequency, 6);
        Assert.Equal(18000, settings.Spectrum.MaxFrequency, 6);
        Assert.Equal(SpectrumSummation.Rms, settings.Spectrum.Summation);
        Assert.Equal(-50, settings.Spectrum.FloorDb, 6);
        Assert.Equal(3, settings.Spectrum.SlopeDbPerOctave, 6);
        Assert.Equal(45, settings.Spectrum.ReleaseDbPerSecond, 6);
        Assert.True(settings.Spectrum.ShowPeaks);
        Assert.Equal(800, settings.Spectrum.PeakHoldMs, 6);
    }

    [Fact]
    public void Normalize_ClampsEveryField()
    {
        var settings = new SpectrumSettings
        {
            BandCount = 1000,
            FftSize = 3000,
            MinFrequency = 1,
            MaxFrequency = 99999,
            Summation = SpectrumSummation.Rms,
            FloorDb = -500,
            SlopeDbPerOctave = 99,
            ReleaseDbPerSecond = 0,
            PeakHoldMs = 9999,
            PeakFallDbPerSecond = 1000,
        };

        SpectrumSettings.Normalize(settings);

        Assert.Equal(96, settings.BandCount);
        Assert.Equal(4096, settings.FftSize);
        Assert.Equal(20, settings.MinFrequency, 6);
        Assert.Equal(24000, settings.MaxFrequency, 6);
        Assert.Equal(-100, settings.FloorDb, 6);
        Assert.Equal(12, settings.SlopeDbPerOctave, 6);
        Assert.Equal(3, settings.ReleaseDbPerSecond, 6);
        Assert.Equal(3000, settings.PeakHoldMs, 6);
        Assert.Equal(240, settings.PeakFallDbPerSecond, 6);
    }

    [Fact]
    public void Normalize_LowerBoundsAreApplied()
    {
        var settings = new SpectrumSettings
        {
            BandCount = -5,
            MinFrequency = 0,
            MaxFrequency = 10,
            FloorDb = 40,
            SlopeDbPerOctave = -99,
            PeakHoldMs = -100,
        };

        SpectrumSettings.Normalize(settings);

        Assert.Equal(8, settings.BandCount);
        Assert.Equal(20, settings.MinFrequency, 6);
        // 最低 500 Hz；此时 500 ≥ 4 × 20，所以不再抬高。
        Assert.Equal(500, settings.MaxFrequency, 6);
        Assert.Equal(-20, settings.FloorDb, 6);
        Assert.Equal(-12, settings.SlopeDbPerOctave, 6);
        Assert.Equal(0, settings.PeakHoldMs, 6);
    }

    [Fact]
    public void Normalize_UnknownKindFallsBackToSpectrum()
    {
        var settings = new VisualizationSettings { Kind = (VisualizerKind)42 };

        VisualizationSettings.Normalize(settings);

        Assert.Equal(VisualizerKind.Spectrum, settings.Kind);
        Assert.NotNull(settings.Spectrum);
    }

    [Fact]
    public void Clone_IsDeepEnoughForSettingsPage()
    {
        var settings = new VisualizationSettings();
        settings.Spectrum.BandCount = 64;

        var clone = settings.Clone();
        clone.Spectrum.BandCount = 24;

        Assert.Equal(64, settings.Spectrum.BandCount);
        Assert.Equal(24, clone.Spectrum.BandCount);
    }

    [Fact]
    public void SpectrumOptions_FromTakesSnapshot()
    {
        var settings = new SpectrumSettings { BandCount = 24, FftSize = 8192 };
        var options = SpectrumOptions.From(settings);

        settings.BandCount = 96;
        settings.FftSize = 1024;

        Assert.Equal(24, options.BandCount);
        Assert.Equal(8192, options.FftSize);
        Assert.Equal(13, options.FftExponent);
        Assert.Equal(25, options.BandEdges.Count);
        Assert.Equal(0, options.CeilingDb, 6);
    }

    [Fact]
    public async Task SettingsFile_RoundTripsVisualizationSection()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        var settings = new JsonSettingsService(paths, new FileLogger(paths));
        await settings.LoadAsync();

        settings.Current.Visualization.Kind = VisualizerKind.Spectrum;
        settings.Current.Visualization.Spectrum.BandCount = 64;
        settings.Current.Visualization.Spectrum.FftSize = 8192;
        settings.Current.Visualization.Spectrum.FloorDb = -60;
        await settings.SaveAsync();

        var reloaded = new JsonSettingsService(paths, new FileLogger(paths));
        await reloaded.LoadAsync();

        Assert.Equal(VisualizerKind.Spectrum, reloaded.Current.Visualization.Kind);
        Assert.Equal(64, reloaded.Current.Visualization.Spectrum.BandCount);
        Assert.Equal(8192, reloaded.Current.Visualization.Spectrum.FftSize);
        Assert.Equal(-60, reloaded.Current.Visualization.Spectrum.FloorDb, 6);
    }

    [Fact]
    public async Task Load_RepairsOutOfRangeVisualizationValues()
    {
        var paths = new AppPaths(_workspace.Combine("state"));
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {
              "schemaVersion": 1,
              "visualization": {
                "kind": "Spectrum",
                "spectrum": {
                  "bandCount": 500,
                  "fftSize": 1234,
                  "minFrequency": 1,
                  "maxFrequency": 99999,
                  "floorDb": -500,
                  "slopeDbPerOctave": 99
                }
              }
            }
            """);

        var settings = new JsonSettingsService(paths, new FileLogger(paths));
        await settings.LoadAsync();

        var spectrum = settings.Current.Visualization.Spectrum;
        Assert.Equal(96, spectrum.BandCount);
        Assert.Equal(4096, spectrum.FftSize);
        Assert.Equal(20, spectrum.MinFrequency, 6);
        Assert.Equal(24000, spectrum.MaxFrequency, 6);
        Assert.Equal(-100, spectrum.FloorDb, 6);
        Assert.Equal(12, spectrum.SlopeDbPerOctave, 6);
    }

    public void Dispose() => _workspace.Dispose();
}
