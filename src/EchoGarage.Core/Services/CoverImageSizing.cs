namespace EchoGarage.Core.Services;

/// <summary>
/// Pure sizing helper for cover art conversion. Cover images are only ever
/// scaled down: the longest edge is limited to <c>maxEdge</c> while the aspect
/// ratio is preserved. Rounding keeps the longest edge at the requested value
/// and lets the other edge land on the nearest integer (e.g. 999/998).
/// </summary>
public static class CoverImageSizing
{
    public static (int Width, int Height) ScaleDown(int width, int height, int maxEdge)
    {
        if (width <= 0 || height <= 0 || maxEdge <= 0)
        {
            return (width, height);
        }

        var longest = Math.Max(width, height);
        if (longest <= maxEdge)
        {
            return (width, height);
        }

        var scale = (double)maxEdge / longest;
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));

        // Rounding can only push an edge past the limit when the source edge is
        // already the longest one and the other edge rounds up; clamp defensively.
        if (Math.Max(targetWidth, targetHeight) > maxEdge)
        {
            if (width >= height)
            {
                targetWidth = maxEdge;
                targetHeight = Math.Max(1, (int)Math.Round(height * (double)maxEdge / width, MidpointRounding.AwayFromZero));
            }
            else
            {
                targetHeight = maxEdge;
                targetWidth = Math.Max(1, (int)Math.Round(width * (double)maxEdge / height, MidpointRounding.AwayFromZero));
            }
        }

        return (targetWidth, targetHeight);
    }
}
