using EchoGarage.Core.Models;

namespace EchoGarage.Core.Abstractions;

/// <summary>Converts one audio file into another format. Implemented by the Infrastructure layer.</summary>
public interface IAudioConverter
{
    Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken = default);
}
