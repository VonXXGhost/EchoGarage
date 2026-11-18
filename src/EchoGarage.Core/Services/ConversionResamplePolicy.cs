using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// Rules for offering a downsample when a lossy format (MP3/AAC) would otherwise receive
/// a source above 48 kHz. A preset with an explicit resample target is never overridden:
/// the user already decided the output sample rate. Only downsampling is ever proposed;
/// sources at or below the limit are converted untouched.
/// </summary>
public static class ConversionResamplePolicy
{
    /// <summary>MP3 cannot encode above 48 kHz; AAC supports more but loses quality, so both prompt here.</summary>
    public const int LossySampleRateLimit = 48000;

    public static bool IsLossyFormat(ConversionFormat format) =>
        format is ConversionFormat.Mp3 or ConversionFormat.Aac;

    /// <summary>True when the preset already pins a resample target (including a target above the source rate).</summary>
    public static bool HasExplicitResample(ConversionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return preset.Resample && preset.TargetSampleRate > 0;
    }

    /// <summary>True when converting <paramref name="preset"/> from this source rate should ask about downsampling.</summary>
    public static bool NeedsDownsamplePrompt(ConversionPreset preset, int sourceSampleRate)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return IsLossyFormat(preset.Format)
            && !HasExplicitResample(preset)
            && sourceSampleRate > LossySampleRateLimit;
    }

    /// <summary>Returns a copy of the preset that resamples to <see cref="LossySampleRateLimit"/>.</summary>
    public static ConversionPreset WithDownsample(ConversionPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var effective = preset.Clone();
        effective.Resample = true;
        effective.TargetSampleRate = LossySampleRateLimit;
        return effective;
    }
}
