using EchoGarage.Core.Services;

namespace EchoGarage.Core.Models;

/// <summary>Output format of a conversion preset.</summary>
public enum ConversionFormat
{
    Aac,
    Flac,
    Mp3,
    Wav
}

/// <summary>Rate control mode. AAC supports VBR/ABR/CBR, MP3 VBR/CBR, FLAC/WAV ignore it.</summary>
public enum ConversionRateMode
{
    Vbr,
    Abr,
    Cbr
}

public enum ConversionOutputMode
{
    SourceFolder,
    CustomFolder
}

/// <summary>What to do when the target file already exists. Ask is the default (prompt once per batch).</summary>
public enum ConversionConflictPolicy
{
    Ask,
    Skip,
    Overwrite,
    Rename
}

public enum ConversionBitDepth
{
    Source,
    Pcm16,
    Pcm24
}

/// <summary>Resampling quality, mapped to SSRC's --profile. High is the default (170 dB / double).</summary>
public enum ConversionResampleProfile
{
    Standard,
    High,
    Insane
}

public enum ConversionStage
{
    Preparing,
    Decoding,
    Resampling,
    Encoding,
    WritingTags,
    Done
}

/// <summary>A saved conversion rule shown in the context menu and the converter settings window.</summary>
public sealed class ConversionPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ConversionFormat Format { get; set; }
    public ConversionRateMode RateMode { get; set; } = ConversionRateMode.Vbr;

    /// <summary>AAC: 0–100 (mapped to Nero -q 0.00–1.00); MP3 VBR: 0–9 (V0 is best); FLAC: 0–8.</summary>
    public int Quality { get; set; }

    /// <summary>ABR / CBR bitrate in kbps; ignored in VBR mode.</summary>
    public int BitrateKbps { get; set; } = 192;

    public ConversionBitDepth BitDepth { get; set; } = ConversionBitDepth.Source;
    public ConversionOutputMode OutputMode { get; set; } = ConversionOutputMode.SourceFolder;
    public string OutputFolder { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = ConversionNaming.DefaultTemplate;
    public ConversionConflictPolicy ConflictPolicy { get; set; } = ConversionConflictPolicy.Ask;

    public bool Resample { get; set; }

    /// <summary>0 means "same as source"; otherwise one of the supported target rates.</summary>
    public int TargetSampleRate { get; set; }

    public ConversionResampleProfile ResampleProfile { get; set; } = ConversionResampleProfile.High;

    public bool CopyTags { get; set; } = true;
    public bool CopyCover { get; set; } = true;

    /// <summary>Optional preset hotkey (same syntax as playback hotkeys), e.g. Ctrl+Alt+1.</summary>
    public string Hotkey { get; set; } = string.Empty;
    public bool HotkeyGlobal { get; set; }

    public ConversionPreset Clone()
    {
        return new ConversionPreset
        {
            Id = Id,
            Name = Name,
            Format = Format,
            RateMode = RateMode,
            Quality = Quality,
            BitrateKbps = BitrateKbps,
            BitDepth = BitDepth,
            OutputMode = OutputMode,
            OutputFolder = OutputFolder,
            FileNameTemplate = FileNameTemplate,
            ConflictPolicy = ConflictPolicy,
            Resample = Resample,
            TargetSampleRate = TargetSampleRate,
            ResampleProfile = ResampleProfile,
            CopyTags = CopyTags,
            CopyCover = CopyCover,
            Hotkey = Hotkey,
            HotkeyGlobal = HotkeyGlobal
        };
    }
}

/// <summary>Explicit encoder/tool paths. Empty means "auto-detect from tools\ or PATH".</summary>
public sealed class ConversionEncoderPaths
{
    public string FlacPath { get; set; } = string.Empty;
    public string LamePath { get; set; } = string.Empty;
    public string SsrcPath { get; set; } = string.Empty;
    public string NeroAacPath { get; set; } = string.Empty;

    public ConversionEncoderPaths Clone()
    {
        return new ConversionEncoderPaths
        {
            FlacPath = FlacPath,
            LamePath = LamePath,
            SsrcPath = SsrcPath,
            NeroAacPath = NeroAacPath
        };
    }
}

/// <summary>Everything the converter needs for one file.</summary>
public sealed record ConversionRequest(string SourcePath, string OutputPath, ConversionPreset Preset, ConversionEncoderPaths Encoders);

public sealed record ConversionProgress(
    int ItemIndex,
    int ItemCount,
    string SourcePath,
    ConversionStage Stage,
    double Fraction,
    string? Detail = null);

public sealed class ConversionResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public string? OutputPath { get; init; }
    public string? Error { get; init; }
    public string? Warning { get; init; }

    public static ConversionResult Ok(string outputPath, string? warning = null) =>
        new() { Success = true, OutputPath = outputPath, Warning = warning };

    public static ConversionResult Skip(string reason) =>
        new() { Skipped = true, Error = reason };

    public static ConversionResult Fail(string error) =>
        new() { Success = false, Error = error };
}
