namespace EchoGarage.Core.Services;

/// <summary>
/// Formats the "index" (#) value shown in the track lists.
/// When the file carries a disc number the value reads "disc.track" (e.g. 1.05);
/// tracks without a usable track number show an empty string instead of the
/// row position, so unknown values are not mistaken for real track numbers.
/// </summary>
public static class TrackIndexFormatter
{
    public static string Format(int discNumber, int trackNumber)
    {
        if (trackNumber <= 0)
        {
            return string.Empty;
        }

        var track = trackNumber.ToString("00");
        return discNumber > 0 ? $"{discNumber}.{track}" : track;
    }
}
