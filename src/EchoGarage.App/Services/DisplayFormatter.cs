using System.Globalization;

namespace EchoGarage.App.Services;

public static class DisplayFormatter
{
    public static string Duration(long milliseconds)
    {
        if (milliseconds <= 0)
        {
            return "-:--";
        }

        var time = TimeSpan.FromMilliseconds(milliseconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    public static string Duration(TimeSpan time) => Duration((long)time.TotalMilliseconds);

    public static string Bitrate(int kbps) => kbps <= 0 ? string.Empty : $"{kbps}K";

    public static string SampleRate(int hz) => hz <= 0
        ? string.Empty
        : hz % 1000 == 0
            ? $"{hz / 1000.0:0}K"
            : $"{hz / 1000.0:0.0}K";

    public static string FileSize(long bytes)
    {
        if (bytes <= 0)
        {
            return string.Empty;
        }

        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Date(DateTime utc)
    {
        if (utc.Year <= 1)
        {
            return string.Empty;
        }

        return utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
    }

    public static string Rating(int rating)
    {
        if (rating <= 0)
        {
            return string.Empty;
        }

        var stars = Math.Clamp(rating, 1, 5);
        return new string('★', stars) + new string('☆', 5 - stars);
    }
}
