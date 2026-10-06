using DataBridge.Core.IO;

namespace DataBridge.Core.Restore;

/// <summary>
/// Strict path rules of the restore. A backup is untrusted input: a relative path read from a manifest or a log, or a
/// name found in the backup, must never make DataBridge read outside the backup or write outside the destination
/// that was chosen for an item ("..", rooted or drive-relative paths, alternate data streams, device names, "\\?\"...).
/// </summary>
public static class RestorePaths
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// True for a single, ordinary file or folder name: not empty, not "." / "..", no separator, no ":" (drive or
    /// alternate data stream), no wildcard or control character, no trailing dot/space, not a device name.
    /// </summary>
    public static bool IsSafeName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(InvalidNameChars) >= 0) return false; // includes \ / : * ? " < > | and control characters
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !DeviceNames.Contains(stem);
    }

    /// <summary>Splits a relative path ("Personal\Documents", "Personal/Documents") into safe segments, or null when unsafe.</summary>
    public static IReadOnlyList<string>? SafeSegments(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var text = relative.Replace('/', '\\');
        if (text.StartsWith('\\') || Path.IsPathRooted(text) || text.Contains(':')) return null;
        var segments = text.Split('\\', StringSplitOptions.None);
        // Accept one trailing separator only ("Personal\Documents\").
        if (segments.Length > 1 && segments[^1].Length == 0) segments = segments[..^1];
        foreach (var s in segments)
            if (!IsSafeName(s)) return null;
        return segments;
    }

    /// <summary>
    /// <paramref name="root"/> + <paramref name="relative"/> when the relative path is safe and the result stays inside
    /// the root (text check, then identity check through existing junctions / links). Null otherwise.
    /// </summary>
    public static string? ResolveInside(string root, string? relative)
    {
        var segments = SafeSegments(relative);
        if (segments is null) return null;
        var normalizedRoot = PathUtil.Normalize(root);
        var full = PathUtil.Normalize(Path.Combine([normalizedRoot, .. segments]));
        if (!PathUtil.IsStrictlyUnder(full, normalizedRoot)) return null;
        // A junction placed inside the backup could point anywhere: compare the real identities too.
        try
        {
            if (!PathIdentity.IsSameOrUnder(full, normalizedRoot)) return null;
        }
        catch
        {
            return null;
        }
        return full;
    }

    /// <summary>Destination file or folder for <paramref name="name"/> inside <paramref name="directory"/>, checked against the allowed root.</summary>
    public static string? ChildInside(string directory, string name, string allowedRoot)
    {
        if (!IsSafeName(name)) return null;
        var full = Path.Combine(directory, name);
        return IsInside(full, allowedRoot) ? full : null;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="allowedRoot"/> or beneath it (normalized text comparison).</summary>
    public static bool IsInside(string path, string allowedRoot)
    {
        try
        {
            return PathUtil.IsSameOrUnder(PathUtil.Normalize(path), PathUtil.Normalize(allowedRoot));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// A fully qualified destination taken from outside (manifest, log, technician): normalized, or null when it is
    /// relative, uses a device / "\\?\" form, an alternate data stream or contains unsafe segments.
    /// </summary>
    public static string? NormalizeAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        if (!Path.IsPathFullyQualified(p)) return null;
        // Only "X:" at the start may hold a colon (no alternate data stream, no second drive).
        if (p.IndexOf(':', 2) >= 0) return null;
        var root = Path.GetPathRoot(p) ?? "";
        var rest = p.Substring(root.Length).TrimStart('\\'); // "C:\" keeps its separator, "\\server\share" does not
        if (rest.Length > 0 && SafeSegments(rest) is null) return null;
        try
        {
            return PathUtil.Normalize(p);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>System locations never used as a restore destination (writing there would need administrator rights anyway).</summary>
    public static bool IsProtectedLocation(string path)
    {
        string?[] protectedRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        foreach (var root in protectedRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (IsInside(path, root)) return true;
        }
        return false;
    }
}
