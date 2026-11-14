using EchoGarage.Core.Models;

namespace EchoGarage.Infrastructure.Conversion;

public enum ConversionTool
{
    Flac,
    Lame,
    Ssrc,
    NeroAac
}

/// <summary>
/// Finds the external tools: an explicit path from the settings wins, then
/// <c>tools\</c> next to the executable, then the executable folder, then PATH.
/// </summary>
public sealed class EncoderLocator
{
    private readonly string _baseDirectory;

    public EncoderLocator(string? baseDirectory = null)
    {
        _baseDirectory = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
    }

    public string? Locate(ConversionFormat format, ConversionEncoderPaths encoders)
    {
        return format switch
        {
            ConversionFormat.Flac => Locate(ConversionTool.Flac, encoders.FlacPath),
            ConversionFormat.Mp3 => Locate(ConversionTool.Lame, encoders.LamePath),
            ConversionFormat.Aac => Locate(ConversionTool.NeroAac, encoders.NeroAacPath),
            _ => null
        };
    }

    public string? Locate(ConversionTool tool, string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (File.Exists(configured))
            {
                return Path.GetFullPath(configured);
            }
        }

        var executableName = GetExecutableName(tool);
        var candidates = new List<string>
        {
            Path.Combine(_baseDirectory, "tools", executableName),
            Path.Combine(_baseDirectory, executableName)
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathVariable))
        {
            foreach (var directory in pathVariable.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    candidates.Add(Path.Combine(directory.Trim('"'), executableName));
                }
                catch (ArgumentException)
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string GetExecutableName(ConversionTool tool)
    {
        return tool switch
        {
            ConversionTool.Flac => "flac.exe",
            ConversionTool.Lame => "lame.exe",
            ConversionTool.Ssrc => "ssrc.exe",
            _ => "neroAacEnc.exe"
        };
    }
}
