using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ConversionResamplePolicyTests
{
    [Theory]
    [InlineData(ConversionFormat.Mp3, 44100, false)]
    [InlineData(ConversionFormat.Mp3, 48000, false)]
    [InlineData(ConversionFormat.Mp3, 48001, true)]
    [InlineData(ConversionFormat.Mp3, 96000, true)]
    [InlineData(ConversionFormat.Aac, 44100, false)]
    [InlineData(ConversionFormat.Aac, 48000, false)]
    [InlineData(ConversionFormat.Aac, 88200, true)]
    [InlineData(ConversionFormat.Aac, 192000, true)]
    [InlineData(ConversionFormat.Flac, 192000, false)]
    [InlineData(ConversionFormat.Wav, 192000, false)]
    public void NeedsDownsamplePrompt_OnlyForLossySourcesAboveLimit(
        ConversionFormat format,
        int sampleRate,
        bool expected)
    {
        var preset = CreatePreset(format);

        Assert.Equal(expected, ConversionResamplePolicy.NeedsDownsamplePrompt(preset, sampleRate));
    }

    [Fact]
    public void NeedsDownsamplePrompt_ExplicitResampleSuppressesPrompt()
    {
        var preset = CreatePreset(ConversionFormat.Mp3);
        preset.Resample = true;
        preset.TargetSampleRate = 44100;

        Assert.False(ConversionResamplePolicy.NeedsDownsamplePrompt(preset, 96000));
    }

    [Fact]
    public void NeedsDownsamplePrompt_ResampleFlagWithoutTargetRateStillPrompts()
    {
        var preset = CreatePreset(ConversionFormat.Aac);
        preset.Resample = true;
        preset.TargetSampleRate = 0;

        Assert.True(ConversionResamplePolicy.NeedsDownsamplePrompt(preset, 88200));
    }

    [Fact]
    public void WithDownsample_ClonesPresetAndKeepsOtherSettings()
    {
        var preset = CreatePreset(ConversionFormat.Mp3);
        preset.Name = "preset";
        preset.BitrateKbps = 320;
        preset.CopyTags = false;

        var effective = ConversionResamplePolicy.WithDownsample(preset);

        Assert.NotSame(preset, effective);
        Assert.True(effective.Resample);
        Assert.Equal(ConversionResamplePolicy.LossySampleRateLimit, effective.TargetSampleRate);
        Assert.Equal("preset", effective.Name);
        Assert.Equal(320, effective.BitrateKbps);
        Assert.False(effective.CopyTags);

        // The user preset must stay untouched so a later batch can ask again.
        Assert.False(preset.Resample);
        Assert.Equal(0, preset.TargetSampleRate);
    }

    private static ConversionPreset CreatePreset(ConversionFormat format) => new()
    {
        Name = "test",
        Format = format,
        Resample = false,
        TargetSampleRate = 0
    };
}
