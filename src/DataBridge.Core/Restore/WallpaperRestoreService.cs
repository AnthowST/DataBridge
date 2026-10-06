using Microsoft.Win32;
using DataBridge.Core.Native;

namespace DataBridge.Core.Restore;

public sealed record WallpaperRestoreResult(bool Copied, bool Applied, string Message, string? StoredPath, string? PreviousWallpaper, bool ApplyRequested = true);

/// <summary>
/// Restores the saved wallpaper of the session: the cached file goes back to
/// %APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper, a stable copy is kept in %APPDATA%\DataBridge\Wallpaper and
/// Windows is asked to apply it (SystemParametersInfo, user setting, no administrator right). If Windows does not
/// refresh, the file is still restored and the technician is told to apply it manually.
/// </summary>
public static class WallpaperRestoreService
{
    public static WallpaperRestoreResult Restore(WallpaperRestoreItem item, string? safetyFolder)
    {
        string? previous = CurrentWallpaper();
        string stored;
        try
        {
            if (!File.Exists(item.SourcePath)) return new(false, false, "Fond d'écran absent de la sauvegarde.", null, previous);

            if (safetyFolder != null && File.Exists(item.TranscodedPath))
            {
                Directory.CreateDirectory(safetyFolder);
                File.Copy(item.TranscodedPath, Path.Combine(safetyFolder, "TranscodedWallpaper"), overwrite: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.TranscodedPath)!);
            CopyOver(item.SourcePath, item.TranscodedPath);

            Directory.CreateDirectory(item.StoreFolder);
            stored = Path.Combine(item.StoreFolder, $"DataBridge-fond-ecran-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ImageExtension(item.SourcePath)}");
            File.Copy(item.SourcePath, stored, overwrite: true);
        }
        catch (Exception ex)
        {
            return new(false, false, "Fond d'écran non restauré : " + ex.Message, null, previous);
        }

        if (!item.Apply)
            return new(true, false, "Fond d'écran restauré (application désactivée pour ce test).", stored, previous, ApplyRequested: false);

        var applied = Apply(stored);
        return new(true, applied,
            applied ? "Fond d'écran restauré et appliqué." : "Fond d'écran restauré. Actualisation Windows à effectuer manuellement.",
            stored, previous);
    }

    /// <summary>Asks Windows to use this image as the desktop wallpaper of the current user and to notify the shell.</summary>
    public static bool Apply(string imagePath)
    {
        try
        {
            return NativeMethods.SystemParametersInfoW(NativeMethods.SPI_SETDESKWALLPAPER, 0, imagePath,
                NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wallpaper path currently configured for the user (HKCU\Control Panel\Desktop\Wallpaper).</summary>
    public static string? CurrentWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyOver(string source, string destination)
    {
        if (File.Exists(destination)) File.SetAttributes(destination, FileAttributes.Normal);
        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>The cache file has no extension: name the stable copy after its real format.</summary>
    internal static string ImageExtension(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            using var stream = File.OpenRead(path);
            var read = stream.Read(head);
            if (read >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ".jpg";
            if (read >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ".png";
            if (read >= 2 && head[0] == 0x42 && head[1] == 0x4D) return ".bmp";
            if (read >= 3 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return ".gif";
        }
        catch
        {
            // fall through
        }
        return ".jpg";
    }
}
