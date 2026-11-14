using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class EncoderCommandLineTests
{
    [Fact]
    public void Nero_Vbr_UsesQualityScale()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Aac, RateMode = ConversionRateMode.Vbr, Quality = 100 };

        var args = EncoderCommandLine.BuildNeroAacArgs(preset, @"C:\tmp\in.wav", @"C:\tmp\out.m4a");

        Assert.Equal(["-q", "1.00", "-if", @"C:\tmp\in.wav", "-of", @"C:\tmp\out.m4a"], args);
    }

    [Fact]
    public void Nero_AbrAndCbr_UseBitrateInBitsPerSecond()
    {
        var abr = EncoderCommandLine.BuildNeroAacArgs(
            new ConversionPreset { Format = ConversionFormat.Aac, RateMode = ConversionRateMode.Abr, BitrateKbps = 192 },
            "in.wav", "out.m4a");
        Assert.Equal(["-br", "192000", "-if", "in.wav", "-of", "out.m4a"], abr);

        var cbr = EncoderCommandLine.BuildNeroAacArgs(
            new ConversionPreset { Format = ConversionFormat.Aac, RateMode = ConversionRateMode.Cbr, BitrateKbps = 256 },
            "in.wav", "out.m4a");
        Assert.Equal(["-cbr", "256000", "-if", "in.wav", "-of", "out.m4a"], cbr);
    }

    [Fact]
    public void Flac_UsesCompressionLevelAndOverwriteFlag()
    {
        var preset = new ConversionPreset { Format = ConversionFormat.Flac, Quality = 5 };

        var normal = EncoderCommandLine.BuildFlacArgs(preset, "in.wav", "out.flac", overwrite: false);
        Assert.Equal(["-5", "-o", "out.flac", "in.wav"], normal);

        var overwrite = EncoderCommandLine.BuildFlacArgs(preset, "in.wav", "out.flac", overwrite: true);
        Assert.Equal(["-5", "-f", "-o", "out.flac", "in.wav"], overwrite);
    }

    [Fact]
    public void Lame_UsesVbrOrCbrMode()
    {
        var vbr = EncoderCommandLine.BuildLameArgs(
            new ConversionPreset { Format = ConversionFormat.Mp3, RateMode = ConversionRateMode.Vbr, Quality = 0 },
            "in.wav", "out.mp3");
        Assert.Equal(["-V", "0", "--silent", "in.wav", "out.mp3"], vbr);

        var cbr = EncoderCommandLine.BuildLameArgs(
            new ConversionPreset { Format = ConversionFormat.Mp3, RateMode = ConversionRateMode.Cbr, BitrateKbps = 320 },
            "in.wav", "out.mp3");
        Assert.Equal(["-b", "320", "--silent", "in.wav", "out.mp3"], cbr);
    }

    [Fact]
    public void Ssrc_UsesRateBitsAndProfile()
    {
        var preset = new ConversionPreset
        {
            Resample = true,
            TargetSampleRate = 48000,
            ResampleProfile = ConversionResampleProfile.Insane
        };

        var args = EncoderCommandLine.BuildSsrcArgs(preset, "in.wav", "out.wav", 24);

        Assert.Equal(["--rate", "48000", "--bits", "24", "--profile", "insane", "in.wav", "out.wav"], args);
    }

    [Fact]
    public void FormatCommandLine_QuotesPathsWithSpaces()
    {
        var command = EncoderCommandLine.FormatCommandLine(
            @"C:\Program Files\tools\flac.exe",
            ["-5", "-o", @"C:\Music Files\out.flac", @"C:\Music Files\in.wav"]);

        Assert.Equal(
            "\"C:\\Program Files\\tools\\flac.exe\" -5 -o \"C:\\Music Files\\out.flac\" \"C:\\Music Files\\in.wav\"",
            command);
    }
}
