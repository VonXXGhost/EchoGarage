using EchoGarage.Core.Models;

namespace EchoGarage.Core.Abstractions;

/// <summary>Converts one audio file into another format. Implemented by the Infrastructure layer.</summary>
public interface IAudioConverter
{
    Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the actual sample rate (Hz) of a source file, or null when it cannot be read.
    /// Used before a lossy conversion to decide whether to offer downsampling.
    /// </summary>
    int? TryProbeSampleRate(string path);
}
