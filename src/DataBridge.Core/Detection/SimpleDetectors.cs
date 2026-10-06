using DataBridge.Core.IO;

namespace DataBridge.Core.Detection;

/// <summary>Outlook signatures folder (%APPDATA%\Microsoft\Signatures).</summary>
public sealed record SignatureInfo(string Path, bool Exists, IReadOnlyList<string> Names)
{
    public static SignatureInfo Detect(string roamingAppData)
    {
        var path = System.IO.Path.Combine(roamingAppData, @"Microsoft\Signatures");
        if (!Directory.Exists(path)) return new SignatureInfo(path, false, []);
        try
        {
            // A signature is a set of .htm/.rtf/.txt files sharing the same name (+ an optional "_files" folder).
            var names = Directory.EnumerateFiles(path)
                .Where(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new SignatureInfo(PathUtil.Normalize(path), true, names);
        }
        catch
        {
            return new SignatureInfo(path, true, []);
        }
    }
}

/// <summary>Current wallpaper cache (%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper, no extension).</summary>
public sealed record WallpaperInfo(string Path, bool Exists, long Size, DateTime? LastWrite)
{
    public static WallpaperInfo Detect(string roamingAppData)
    {
        var path = System.IO.Path.Combine(roamingAppData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new WallpaperInfo(path, true, fi.Length, fi.LastWriteTime) : new WallpaperInfo(path, false, 0, null);
        }
        catch
        {
            return new WallpaperInfo(path, false, 0, null);
        }
    }
}
