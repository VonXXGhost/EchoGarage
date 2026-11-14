using EchoGarage.Core.Abstractions;
using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>Creates and validates conversion presets (built-in defaults + settings normalization).</summary>
public static class ConversionPresetFactory
{
    public static List<ConversionPreset> CreateBuiltInPresets() =>
    [
        new()
        {
            Id = "builtin-mp3-320",
            Name = "MP3 · 320 kbps",
            Format = ConversionFormat.Mp3,
            RateMode = ConversionRateMode.Cbr,
            BitrateKbps = 320,
            BitDepth = ConversionBitDepth.Pcm16,
            FileNameTemplate = ConversionNaming.DefaultTemplate
        },
        new()
        {
            Id = "builtin-flac-5",
            Name = "FLAC · 无损（级别 5）",
            Format = ConversionFormat.Flac,
            Quality = 5,
            BitDepth = ConversionBitDepth.Source,
            FileNameTemplate = ConversionNaming.DefaultTemplate
        },
        new()
        {
            Id = "builtin-aac-q100",
            Name = "AAC · Nero Q1.00",
            Format = ConversionFormat.Aac,
            RateMode = ConversionRateMode.Vbr,
            Quality = 100,
            BitDepth = ConversionBitDepth.Pcm16,
            FileNameTemplate = ConversionNaming.DefaultTemplate
        },
        new()
        {
            Id = "builtin-wav-16",
            Name = "WAV · 16-bit",
            Format = ConversionFormat.Wav,
            BitDepth = ConversionBitDepth.Pcm16,
            FileNameTemplate = ConversionNaming.DefaultTemplate
        }
    ];

    public static ConversionPreset CreateDefaultPreset() => new()
    {
        Name = "新预设",
        Format = ConversionFormat.Mp3,
        RateMode = ConversionRateMode.Vbr,
        Quality = 0,
        BitDepth = ConversionBitDepth.Pcm16,
        FileNameTemplate = ConversionNaming.DefaultTemplate
    };

    /// <summary>
    /// Validates the preset list: unique names/ids, format-specific parameter ranges,
    /// canonical hotkey text and duplicate-hotkey removal.
    /// </summary>
    public static List<ConversionPreset> NormalizeAll(
        IEnumerable<ConversionPreset>? presets,
        IEnumerable<HotkeyBinding>? playbackHotkeys = null,
        IAppLogger? logger = null)
    {
        if (presets is null)
        {
            return CreateBuiltInPresets();
        }

        var usedGestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (playbackHotkeys is not null)
        {
            foreach (var binding in playbackHotkeys)
            {
                if (HotkeyGesture.TryParse(binding.Gesture, out var gesture) && gesture is not null)
                {
                    usedGestures.Add(gesture.ToString());
                }
            }
        }

        var result = new List<ConversionPreset>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        foreach (var preset in presets)
        {
            if (preset is null)
            {
                continue;
            }

            index++;
            var normalized = preset.Clone();

            normalized.Name = normalized.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized.Name))
            {
                normalized.Name = $"预设 {index}";
            }

            normalized.Name = MakeUniqueName(normalized.Name, usedNames);
            usedNames.Add(normalized.Name);

            normalized.Id = normalized.Id?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized.Id) || !usedIds.Add(normalized.Id))
            {
                normalized.Id = Guid.NewGuid().ToString("N");
                usedIds.Add(normalized.Id);
            }

            NormalizeFormatParameters(normalized);

            normalized.OutputFolder = normalized.OutputFolder?.Trim() ?? string.Empty;
            normalized.FileNameTemplate = normalized.FileNameTemplate?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalized.FileNameTemplate))
            {
                normalized.FileNameTemplate = ConversionNaming.DefaultTemplate;
            }

            if (normalized.FileNameTemplate.Length > ConversionNaming.MaxTemplateLength)
            {
                normalized.FileNameTemplate = normalized.FileNameTemplate[..ConversionNaming.MaxTemplateLength];
            }

            if (!Enum.IsDefined(normalized.ConflictPolicy))
            {
                normalized.ConflictPolicy = ConversionConflictPolicy.Ask;
            }

            if (!Enum.IsDefined(normalized.OutputMode))
            {
                normalized.OutputMode = ConversionOutputMode.SourceFolder;
            }

            if (!Enum.IsDefined(normalized.ResampleProfile))
            {
                normalized.ResampleProfile = ConversionResampleProfile.High;
            }

            if (!ConversionNaming.SupportedSampleRates.Contains(normalized.TargetSampleRate))
            {
                normalized.TargetSampleRate = 0;
            }

            NormalizeHotkey(normalized, usedGestures, logger);
            result.Add(normalized);
        }

        return result;
    }

    public static ConversionEncoderPaths NormalizeEncoders(ConversionEncoderPaths? paths)
    {
        if (paths is null)
        {
            return new ConversionEncoderPaths();
        }

        return new ConversionEncoderPaths
        {
            FlacPath = NormalizePath(paths.FlacPath),
            LamePath = NormalizePath(paths.LamePath),
            SsrcPath = NormalizePath(paths.SsrcPath),
            NeroAacPath = NormalizePath(paths.NeroAacPath)
        };
    }

    private static void NormalizeFormatParameters(ConversionPreset preset)
    {
        preset.BitrateKbps = Math.Clamp(preset.BitrateKbps, 32, 512);

        switch (preset.Format)
        {
            case ConversionFormat.Aac:
                preset.Quality = Math.Clamp(preset.Quality, 0, 100);
                preset.BitrateKbps = Math.Clamp(preset.BitrateKbps, 32, 512);
                preset.BitDepth = ConversionBitDepth.Pcm16;
                if (!Enum.IsDefined(preset.RateMode))
                {
                    preset.RateMode = ConversionRateMode.Vbr;
                }

                break;

            case ConversionFormat.Mp3:
                preset.Quality = Math.Clamp(preset.Quality, 0, 9);
                preset.BitrateKbps = Math.Clamp(preset.BitrateKbps, 32, 320);
                preset.BitDepth = ConversionBitDepth.Pcm16;
                if (preset.RateMode == ConversionRateMode.Abr || !Enum.IsDefined(preset.RateMode))
                {
                    preset.RateMode = ConversionRateMode.Cbr;
                }

                break;

            case ConversionFormat.Flac:
                preset.Quality = Math.Clamp(preset.Quality, 0, 8);
                preset.RateMode = ConversionRateMode.Vbr;
                if (!Enum.IsDefined(preset.BitDepth))
                {
                    preset.BitDepth = ConversionBitDepth.Source;
                }

                break;

            case ConversionFormat.Wav:
                preset.RateMode = ConversionRateMode.Vbr;
                preset.Quality = 0;
                if (preset.BitDepth == ConversionBitDepth.Source || !Enum.IsDefined(preset.BitDepth))
                {
                    preset.BitDepth = ConversionBitDepth.Pcm16;
                }

                break;

            default:
                preset.Format = ConversionFormat.Mp3;
                NormalizeFormatParameters(preset);
                break;
        }
    }

    private static void NormalizeHotkey(ConversionPreset preset, HashSet<string> usedGestures, IAppLogger? logger)
    {
        var raw = preset.Hotkey?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            preset.Hotkey = string.Empty;
            preset.HotkeyGlobal = false;
            return;
        }

        if (!HotkeyGesture.TryParse(raw, out var gesture) || gesture is null)
        {
            logger?.Warn($"Conversion preset \"{preset.Name}\" has an invalid hotkey \"{raw}\"; clearing it.");
            preset.Hotkey = string.Empty;
            preset.HotkeyGlobal = false;
            return;
        }

        var canonical = gesture.ToString();
        if (!usedGestures.Add(canonical))
        {
            logger?.Warn($"Conversion preset \"{preset.Name}\" hotkey \"{canonical}\" is already used; clearing it.");
            preset.Hotkey = string.Empty;
            preset.HotkeyGlobal = false;
            return;
        }

        preset.Hotkey = canonical;
    }

    private static string MakeUniqueName(string name, HashSet<string> usedNames)
    {
        if (!usedNames.Contains(name))
        {
            return name;
        }

        for (var i = 2; i < 10000; i++)
        {
            var candidate = $"{name} ({i})";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return name;
    }

    private static string NormalizePath(string? path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }
}
