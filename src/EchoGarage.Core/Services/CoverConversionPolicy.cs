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

    /// <summary>
    /// True when the cover is already JPEG at exactly the configured maximum edge, i.e. the
    /// current conversion settings would only re-encode it at the same resolution. Such an
    /// image has most likely been produced by an earlier run of the very same conversion, so
    /// another pass can only trade quality for a slightly smaller file. The tag editor asks the
    /// user before doing that instead of silently compressing the cover again.
    /// </summary>
    public static bool IsAlreadyAtTargetEdge(
        bool sourceIsJpeg,
        int sourceWidth,
        int sourceHeight,
        int targetMaxEdge)
    {
        if (!sourceIsJpeg || targetMaxEdge <= 0 || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return false;
        }

        return Math.Max(sourceWidth, sourceHeight) == targetMaxEdge;
    }
}
