using System.Globalization;
using System.Text;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>Builds command-line argument lists for the external encoders and SSRC resampler.</summary>
public static class EncoderCommandLine
{
    public static IReadOnlyList<string> BuildNeroAacArgs(ConversionPreset preset, string inputWav, string outputFile)
    {
        var args = new List<string>();
        switch (preset.RateMode)
        {
            case ConversionRateMode.Abr:
                args.Add("-br");
                args.Add((preset.BitrateKbps * 1000).ToString(CultureInfo.InvariantCulture));
                break;
            case ConversionRateMode.Cbr:
                args.Add("-cbr");
                args.Add((preset.BitrateKbps * 1000).ToString(CultureInfo.InvariantCulture));
                break;
            default:
                args.Add("-q");
                args.Add((Math.Clamp(preset.Quality, 0, 100) / 100.0).ToString("0.00", CultureInfo.InvariantCulture));
                break;
        }

        args.Add("-if");
        args.Add(inputWav);
        args.Add("-of");
        args.Add(outputFile);
        return args;
    }

    public static IReadOnlyList<string> BuildFlacArgs(ConversionPreset preset, string inputWav, string outputFile, bool overwrite)
    {
        var args = new List<string>
        {
            $"-{Math.Clamp(preset.Quality, 0, 8)}"
        };

        if (overwrite)
        {
            args.Add("-f");
        }

        args.Add("-o");
        args.Add(outputFile);
        args.Add(inputWav);
        return args;
    }

    public static IReadOnlyList<string> BuildLameArgs(ConversionPreset preset, string inputWav, string outputFile)
    {
        var args = new List<string>();
        if (preset.RateMode == ConversionRateMode.Cbr)
        {
            args.Add("-b");
            args.Add(Math.Clamp(preset.BitrateKbps, 32, 320).ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            args.Add("-V");
            args.Add(Math.Clamp(preset.Quality, 0, 9).ToString(CultureInfo.InvariantCulture));
        }

        args.Add("--silent");
        args.Add(inputWav);
        args.Add(outputFile);
        return args;
    }

    public static IReadOnlyList<string> BuildSsrcArgs(ConversionPreset preset, string inputWav, string outputFile, int bits)
    {
        return BuildSsrcArgs(
            Math.Max(0, preset.TargetSampleRate),
            preset.ResampleProfile,
            inputWav,
            outputFile,
            bits);
    }

    /// <summary>
    /// Builds SSRC arguments for an explicit rate/profile pair. Used by the latency probe
    /// so it can measure the resampler delay without constructing a full preset.
    /// </summary>
    public static IReadOnlyList<string> BuildSsrcArgs(
        int targetSampleRate,
        ConversionResampleProfile profile,
        string inputWav,
        string outputFile,
        int bits)
    {
        return
        [
            "--rate", Math.Max(0, targetSampleRate).ToString(CultureInfo.InvariantCulture),
            "--bits", Math.Clamp(bits, 16, 24).ToString(CultureInfo.InvariantCulture),
            "--profile", profile switch
            {
                ConversionResampleProfile.Standard => "standard",
                ConversionResampleProfile.Insane => "insane",
                _ => "high"
            },
            inputWav,
            outputFile
        ];
    }

    /// <summary>Renders a human-readable command line for logs and error messages.</summary>
    public static string FormatCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(Quote(executablePath));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        return value.IndexOfAny([' ', '\t', '"']) >= 0
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;
    }
}
