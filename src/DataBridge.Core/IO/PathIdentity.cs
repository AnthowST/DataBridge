using System.Text;
using DataBridge.Core.Native;

namespace DataBridge.Core.IO;

/// <summary>
/// Comparable identity of a path, to decide whether two paths overlap whatever the way they are written:
/// "..", "/", trailing separators, letter case, "\\?\" prefixes, mapped network drives (Z:\ → \\server\share)
/// and, for local paths, junctions / symbolic links / SUBST drives through the deepest existing folder.
/// Network paths are never touched on the network (no slow lookups): only their text is normalized.
/// </summary>
public static class PathIdentity
{
    /// <summary>Resolves a mapped drive letter to its UNC root (null when not a network drive). Replaceable in tests.</summary>
    internal static Func<char, string?> NetworkDriveResolver { get; set; } = DefaultNetworkDrive;

    public static string Canonical(string path)
    {
        var p = StripDevicePrefix(path.Trim().Replace('/', '\\'));
        try
        {
            p = Path.GetFullPath(p);
        }
        catch
        {
            // Invalid characters: compare the raw text.
        }

        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]) && NetworkDriveResolver(char.ToUpperInvariant(p[0])) is { } unc)
            p = unc.TrimEnd('\\') + p.Substring(2);

        if (!p.StartsWith(@"\\", StringComparison.Ordinal))
            p = ResolveExistingPart(p);

        return TrimEnd(p);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (identities compared).</summary>
    public static bool IsSameOrUnder(string path, string root) => PathUtil.IsSameOrUnder(Canonical(path), Canonical(root));

    internal static string StripDevicePrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + p.Substring(8);
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            return p.Substring(4);
        return p;
    }

    private static string TrimEnd(string p)
    {
        if (p.Length == 3 && p[1] == ':') return p; // "C:\"
        return p.Length > 3 ? p.TrimEnd('\\') : p;
    }

    /// <summary>Final path of the deepest existing ancestor (follows junctions, links, SUBST), plus the missing tail.</summary>
    private static string ResolveExistingPart(string p)
    {
        var tail = new Stack<string>();
        var current = p;
        for (var guard = 0; guard < 512 && current != null; guard++)
        {
            if (Directory.Exists(current) || File.Exists(current))
            {
                var final = FinalPath(current);
                if (final is null) return p;
                foreach (var segment in tail) final = Path.Combine(final, segment);
                return final;
            }
            var name = Path.GetFileName(current.TrimEnd('\\'));
            var parent = Path.GetDirectoryName(current.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name) || parent is null) break;
            tail.Push(name);
            current = parent;
        }
        return p;
    }

    private static string? FinalPath(string existing)
    {
        try
        {
            using var handle = NativeMethods.CreateFileW(PathUtil.ToExtended(existing), 0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE | NativeMethods.FILE_SHARE_DELETE,
                IntPtr.Zero, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(1024);
            var length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder(length + 1);
                length = NativeMethods.GetFinalPathNameByHandleW(handle, buffer, buffer.Capacity, 0);
            }
            return length > 0 && length <= buffer.Capacity ? StripDevicePrefix(buffer.ToString()) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? DefaultNetworkDrive(char letter)
    {
        try
        {
            if (new DriveInfo(letter.ToString()).DriveType != DriveType.Network) return null;
            var sb = new StringBuilder(512);
            var len = sb.Capacity;
            return NativeMethods.WNetGetConnectionW(letter + ":", sb, ref len) == 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }
}
