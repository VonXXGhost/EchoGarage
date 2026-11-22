using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class OutputVolumeResolverTests
{
    private const string SpeakersId = "{0.0.0.00000000}.{demo-speakers}";
    private const string HeadphonesId = "{0.0.0.00000000}.{demo-headphones}";

    private static AppSettings Settings() => new() { Volume = 0.6 };

    [Fact]
    public void ResolveDisplayVolume_NoMemory_FallsBackToGlobalVolume()
    {
        var settings = Settings();

        var volume = OutputVolumeResolver.ResolveDisplayVolume(settings, SpeakersId, OutputMode.Shared);

        Assert.Equal(0.6, volume, 6);
    }

    [Fact]
    public void ResolveDisplayVolume_UsesPerDeviceAndModeMemory()
    {
        var settings = Settings();
        settings.DeviceVolumes[SpeakersId] = new OutputDeviceVolume { Shared = 0.25, Exclusive = 0.8 };

        Assert.Equal(0.25, OutputVolumeResolver.ResolveDisplayVolume(settings, SpeakersId, OutputMode.Shared), 6);
        Assert.Equal(0.8, OutputVolumeResolver.ResolveDisplayVolume(settings, SpeakersId, OutputMode.Exclusive), 6);
    }

    [Fact]
    public void ResolveDisplayVolume_MissingMode_FallsBackToGlobalVolume()
    {
        var settings = Settings();
        settings.DeviceVolumes[SpeakersId] = new OutputDeviceVolume { Shared = 0.25 };

        var volume = OutputVolumeResolver.ResolveDisplayVolume(settings, SpeakersId, OutputMode.Exclusive);

        Assert.Equal(0.6, volume, 6);
    }

    [Fact]
    public void ResolveDisplayVolume_DeviceIdMatchIsCaseInsensitive()
    {
        var settings = Settings();
        settings.DeviceVolumes[SpeakersId] = new OutputDeviceVolume { Shared = 0.4 };

        var volume = OutputVolumeResolver.ResolveDisplayVolume(
            settings, SpeakersId.ToUpperInvariant(), OutputMode.Shared);

        Assert.Equal(0.4, volume, 6);
    }

    [Fact]
    public void ResolveDisplayVolume_OtherDeviceOrNoDevice_FallsBackToGlobalVolume()
    {
        var settings = Settings();
        settings.DeviceVolumes[HeadphonesId] = new OutputDeviceVolume { Shared = 0.1 };

        Assert.Equal(0.6, OutputVolumeResolver.ResolveDisplayVolume(settings, SpeakersId, OutputMode.Shared), 6);
        Assert.Equal(0.6, OutputVolumeResolver.ResolveDisplayVolume(settings, null, OutputMode.Shared), 6);
        Assert.Equal(0.6, OutputVolumeResolver.ResolveDisplayVolume(settings, "   ", OutputMode.Shared), 6);
    }

    [Fact]
    public void Remember_UpsertsDeviceModeAndUpdatesGlobalFallback()
    {
        var settings = Settings();

        OutputVolumeResolver.Remember(settings, SpeakersId, OutputMode.Shared, 0.35);
        OutputVolumeResolver.Remember(settings, SpeakersId, OutputMode.Exclusive, 0.7);

        var memory = Assert.Single(settings.DeviceVolumes).Value;
        Assert.Equal(0.35, memory.Shared!.Value, 6);
        Assert.Equal(0.7, memory.Exclusive!.Value, 6);
        Assert.Equal(0.7, settings.Volume, 6);
    }

    [Fact]
    public void Remember_OnlyChangesTheSelectedMode()
    {
        var settings = Settings();
        settings.DeviceVolumes[SpeakersId] = new OutputDeviceVolume { Shared = 0.2, Exclusive = 0.9 };

        OutputVolumeResolver.Remember(settings, SpeakersId, OutputMode.Shared, 0.45);

        var memory = settings.DeviceVolumes[SpeakersId];
        Assert.Equal(0.45, memory.Shared!.Value, 6);
        Assert.Equal(0.9, memory.Exclusive!.Value, 6);
    }

    [Fact]
    public void Remember_BlankDevice_OnlyUpdatesGlobalVolume()
    {
        var settings = Settings();

        OutputVolumeResolver.Remember(settings, "   ", OutputMode.Shared, 0.3);

        Assert.Empty(settings.DeviceVolumes);
        Assert.Equal(0.3, settings.Volume, 6);
    }

    [Fact]
    public void Remember_ClampsDisplayVolumeToUnitRange()
    {
        var settings = Settings();

        OutputVolumeResolver.Remember(settings, SpeakersId, OutputMode.Shared, 1.7);

        Assert.Equal(1.0, settings.DeviceVolumes[SpeakersId].Shared!.Value, 6);
        Assert.Equal(1.0, settings.Volume, 6);
    }

    [Fact]
    public void GetEffectiveGain_MultipliesDisplayByCeiling()
    {
        Assert.Equal(0.25, OutputVolumeResolver.GetEffectiveGain(0.5, 0.5), 6);
        Assert.Equal(0.025, OutputVolumeResolver.GetEffectiveGain(0.5, OutputVolumeResolver.MinCeiling), 6);
        Assert.Equal(0.0, OutputVolumeResolver.GetEffectiveGain(-0.2, 1.0), 6);
    }

    [Fact]
    public void GetEffectiveGain_ClampsCeilingRange()
    {
        // 上限下限 5%：即使配置是 0（旧版本手改文件）也不能完全无声。
        Assert.Equal(0.05, OutputVolumeResolver.GetEffectiveGain(1.0, 0.0), 6);
        Assert.Equal(1.0, OutputVolumeResolver.GetEffectiveGain(1.0, 1.5), 6);
    }

    [Fact]
    public void ClampCeiling_HandlesInvalidValues()
    {
        Assert.Equal(1.0, OutputVolumeResolver.ClampCeiling(double.NaN), 6);
        Assert.Equal(1.0, OutputVolumeResolver.ClampCeiling(double.PositiveInfinity), 6);
        Assert.Equal(OutputVolumeResolver.MinCeiling, OutputVolumeResolver.ClampCeiling(0.01), 6);
        Assert.Equal(0.4, OutputVolumeResolver.ClampCeiling(0.4), 6);
    }

    [Fact]
    public void GetCeiling_ReturnsModeSpecificValue()
    {
        var settings = Settings();
        settings.VolumeCeilings = new OutputVolumeCeilings { Shared = 0.5, Exclusive = 0.2 };

        Assert.Equal(0.5, OutputVolumeResolver.GetCeiling(settings, OutputMode.Shared), 6);
        Assert.Equal(0.2, OutputVolumeResolver.GetCeiling(settings, OutputMode.Exclusive), 6);
    }

    [Fact]
    public void NormalizeMemories_DropsInvalidEntriesAndClampsValues()
    {
        var source = new Dictionary<string, OutputDeviceVolume>(StringComparer.Ordinal)
        {
            ["  " + SpeakersId + "  "] = new() { Shared = 1.4, Exclusive = -0.3 },
            ["   "] = new() { Shared = 0.5 },
            [HeadphonesId] = new(),
            ["{0.0.0.00000000}.{demo-hdmi}"] = null!,
        };

        var normalized = OutputVolumeResolver.NormalizeMemories(source);

        Assert.Single(normalized);
        var speakers = normalized[SpeakersId];
        Assert.Equal(1.0, speakers.Shared!.Value, 6);
        Assert.Equal(0.0, speakers.Exclusive!.Value, 6);
        // 空白键、两个值都无效的条目、以及值为 null 的条目都被丢弃。
        Assert.DoesNotContain(HeadphonesId, normalized.Keys);
    }

    [Fact]
    public void NormalizeMemories_DeduplicatesKeysCaseInsensitively()
    {
        var source = new Dictionary<string, OutputDeviceVolume>(StringComparer.Ordinal)
        {
            [SpeakersId] = new() { Shared = 0.3 },
            [SpeakersId.ToUpperInvariant()] = new() { Shared = 0.5 },
        };

        var normalized = OutputVolumeResolver.NormalizeMemories(source);

        // 大小写不同但实际是同一设备：只保留一个条目。
        Assert.Single(normalized);
        Assert.True(normalized.ContainsKey(SpeakersId));
    }

    [Fact]
    public void NormalizeMemories_LimitsDeviceCount()
    {
        var source = new Dictionary<string, OutputDeviceVolume>(StringComparer.Ordinal);
        for (var i = 0; i < OutputVolumeResolver.MaxRememberedDevices + 5; i++)
        {
            source[$"{{0.0.0.00000000}}.{{demo-device-{i}}}"] = new() { Shared = 0.5 };
        }

        var normalized = OutputVolumeResolver.NormalizeMemories(source);

        Assert.Equal(OutputVolumeResolver.MaxRememberedDevices, normalized.Count);
    }

    [Fact]
    public void NormalizeMemories_NullOrEmpty_ReturnsEmptyDictionary()
    {
        Assert.Empty(OutputVolumeResolver.NormalizeMemories(null));
        Assert.Empty(OutputVolumeResolver.NormalizeMemories(new Dictionary<string, OutputDeviceVolume>()));
    }
}
