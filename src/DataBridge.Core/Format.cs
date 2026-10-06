using System.Globalization;

namespace DataBridge.Core;

/// <summary>Human readable sizes, counts and durations (French units: o, Ko, Mo, Go, To).</summary>
public static class Format
{
    public const long OneGiB = 1024L * 1024 * 1024;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly NumberFormatInfo Grouping = new() { NumberGroupSeparator = " ", NumberDecimalDigits = 0 };

    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes} o";
        double kb = bytes / 1024.0;
        if (kb < 1000) return string.Create(Inv, $"{Math.Max(1, Math.Round(kb)):0} Ko");
        double mb = kb / 1024.0;
        if (mb < 1000) return mb < 10 ? string.Create(Inv, $"{mb:0.0} Mo") : string.Create(Inv, $"{mb:0} Mo");
        double gb = mb / 1024.0;
        if (gb < 1000) return string.Create(Inv, $"{gb:0.0} Go");
        return string.Create(Inv, $"{gb / 1024.0:0.0} To");
    }

    public static string Count(long n) => n.ToString("N0", Grouping);

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes:00}:{d.Seconds:00}";

    public static string Plural(long n, string singular, string plural) => $"{Count(n)} {(n > 1 ? plural : singular)}";
}
