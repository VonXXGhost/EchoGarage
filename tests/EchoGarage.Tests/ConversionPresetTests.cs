using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ConversionPresetTests
{
    [Fact]
    public void BuiltInPresets_AreValidAndCoverAllFormats()
    {
        var presets = ConversionPresetFactory.CreateBuiltInPresets();

        Assert.Equal(4, presets.Count);
        Assert.Contains(presets, p => p.Format == ConversionFormat.Mp3 && p.RateMode == ConversionRateMode.Cbr && p.BitrateKbps == 320);
        Assert.Contains(presets, p => p.Format == ConversionFormat.Aac && p.Quality == 100);
        Assert.Contains(presets, p => p.Format == ConversionFormat.Flac && p.Quality == 5);
        Assert.Contains(presets, p => p.Format == ConversionFormat.Wav && p.BitDepth == ConversionBitDepth.Pcm16);
        Assert.All(presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
        Assert.All(presets, p => Assert.Equal(ConversionNaming.DefaultTemplate, p.FileNameTemplate));
    }

    [Fact]
    public void Normalize_ClampsFormatSpecificParameters()
    {
        var presets = ConversionPresetFactory.NormalizeAll(
        [
            new ConversionPreset { Format = ConversionFormat.Aac, Quality = 500, BitrateKbps = 9999 },
            new ConversionPreset { Format = ConversionFormat.Mp3, Quality = 42, RateMode = ConversionRateMode.Abr, BitrateKbps = 999 },
            new ConversionPreset { Format = ConversionFormat.Flac, Quality = 99 },
            new ConversionPreset { Format = ConversionFormat.Wav, BitDepth = ConversionBitDepth.Source }
        ]);

        Assert.Equal(100, presets[0].Quality);
        Assert.Equal(512, presets[0].BitrateKbps);
        Assert.Equal(9, presets[1].Quality);
        Assert.Equal(ConversionRateMode.Cbr, presets[1].RateMode);
        Assert.Equal(320, presets[1].BitrateKbps);
        Assert.Equal(8, presets[2].Quality);
        Assert.Equal(ConversionBitDepth.Pcm16, presets[3].BitDepth);
    }

    [Fact]
    public void Normalize_KeepsUniqueNamesAndIds()
    {
        var presets = ConversionPresetFactory.NormalizeAll(
        [
            new ConversionPreset { Id = "same", Name = "Preset" },
            new ConversionPreset { Id = "same", Name = "Preset" },
            new ConversionPreset { Id = "", Name = "  " }
        ]);

        Assert.Equal(3, presets.Count);
        Assert.Equal("Preset", presets[0].Name);
        Assert.Equal("Preset (2)", presets[1].Name);
        Assert.Equal("预设 3", presets[2].Name);
        Assert.NotEqual(presets[0].Id, presets[1].Id);
        Assert.NotEqual(presets[1].Id, presets[2].Id);
    }

    [Fact]
    public void Normalize_CanonicalizesHotkeysAndDropsDuplicates()
    {
        var playback = new List<HotkeyBinding>
        {
            new() { Action = HotkeyAction.PlayPause, Gesture = "Ctrl+Alt+Space" }
        };

        var presets = ConversionPresetFactory.NormalizeAll(
        [
            new ConversionPreset { Name = "A", Hotkey = "ctrl+alt+1" },
            new ConversionPreset { Name = "B", Hotkey = "Ctrl+Alt+1" },
            new ConversionPreset { Name = "C", Hotkey = "Ctrl+Alt+Space" },
            new ConversionPreset { Name = "D", Hotkey = "not a key" }
        ], playback);

        Assert.Equal("Ctrl+Alt+1", presets[0].Hotkey);
        Assert.Equal(string.Empty, presets[1].Hotkey);
        Assert.Equal(string.Empty, presets[2].Hotkey);
        Assert.Equal(string.Empty, presets[3].Hotkey);
    }

    [Fact]
    public void Normalize_InvalidSampleRateFallsBackToZero()
    {
        var presets = ConversionPresetFactory.NormalizeAll(
        [
            new ConversionPreset { TargetSampleRate = 12345 }
        ]);

        Assert.Equal(0, presets[0].TargetSampleRate);
    }

    [Fact]
    public void NormalizeEncoders_TrimsAndExpandsEnvironmentVariables()
    {
        var paths = ConversionPresetFactory.NormalizeEncoders(new ConversionEncoderPaths
        {
            FlacPath = "  %TEMP%\\flac.exe  ",
            NeroAacPath = "  "
        });

        Assert.Equal(Path.Combine(Path.GetTempPath().TrimEnd('\\'), "flac.exe"), paths.FlacPath);
        Assert.Equal(string.Empty, paths.NeroAacPath);
    }
}
