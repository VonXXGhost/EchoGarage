namespace EchoGarage.Core.Services;

/// <summary>
/// Decides whether a cover image should be re-encoded as JPEG.
/// Rules:
///  - conversion only shrinks or keeps the resolution, it never upscales;
///  - when the image must be downscaled the conversion always runs (that is the
///    main way to save space);
///  - an image that is already JPEG and smaller than the target edge is kept as is;
///  - when no scaling is required (same resolution condition) the converted
///    bytes are only used when they are strictly smaller than the original.
/// </summary>
public static class CoverConversionPolicy
{
    public static bool ShouldConvert(
        bool sourceIsJpeg,
        int sourceWidth,
        int sourceHeight,
        int targetMaxEdge,
        long originalSize,
        long convertedSize)
    {
        if (convertedSize <= 0)
        {
            return false;
        }

        var longest = Math.Max(sourceWidth, sourceHeight);
        var requiresScale = targetMaxEdge > 0 && longest > targetMaxEdge;
        if (requiresScale)
        {
            return true;
        }

        if (targetMaxEdge <= 0)
        {
            // No resolution limit: re-encoding an already-JPEG image has no benefit.
            return !sourceIsJpeg && convertedSize < originalSize;
        }

        if (sourceIsJpeg && longest < targetMaxEdge)
        {
            return false;
        }

        return convertedSize < originalSize;
    }
}
