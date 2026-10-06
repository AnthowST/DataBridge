namespace DataBridge.Core.IO;

public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private const StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    /// <summary>Full path without trailing separator (except for roots such as "C:\").</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 3 && full.EndsWith('\\') && !IsUncShareRoot(full))
            full = full.TrimEnd('\\');
        return full;
    }

    private static bool IsUncShareRoot(string p)
    {
        // \\server\share\ : keep as-is, it is a root.
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var parts = p.TrimEnd('\\').Substring(2).Split('\\');
        return parts.Length <= 2;
    }

    public static bool AreSame(string a, string b) => string.Equals(a, b, Cmp);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/>.</summary>
    public static bool IsStrictlyUnder(string path, string root)
    {
        if (path.Length <= root.Length) return false;
        if (!path.StartsWith(root, Cmp)) return false;
        return root.EndsWith('\\') || path[root.Length] == '\\';
    }

    public static bool IsSameOrUnder(string path, string root) => AreSame(path, root) || IsStrictlyUnder(path, root);

    public static string? Parent(string path) => Path.GetDirectoryName(path);

    /// <summary>Relative path from root ("" when equal).</summary>
    public static string Relative(string path, string root)
    {
        if (AreSame(path, root)) return "";
        var start = root.EndsWith('\\') ? root.Length : root.Length + 1;
        return path.Substring(start);
    }

    /// <summary>Extended-length form ("\\?\C:\..." or "\\?\UNC\server\share\...") so paths over 260 chars work.</summary>
    public static string ToExtended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>Makes a single path segment valid for Windows (no reserved characters, no trailing dot/space).</summary>
    public static string SanitizeSegment(string name, string fallback = "element")
    {
        var chars = name.Select(c => InvalidNameChars.Contains(c) ? '-' : c).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');
        if (result.Length == 0) result = fallback;
        var upper = result.ToUpperInvariant();
        string[] reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
        if (reserved.Contains(upper)) result = "_" + result;
        return result;
    }

    /// <summary>Last segment of a path, with a readable name for drive roots ("Lecteur D").</summary>
    public static string DisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var name = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(name)) return name;
        if (trimmed.Length == 2 && trimmed[1] == ':') return "Lecteur " + char.ToUpperInvariant(trimmed[0]);
        return trimmed.Replace('\\', '_').Trim('_');
    }
}
