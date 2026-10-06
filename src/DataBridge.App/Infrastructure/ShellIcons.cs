using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DataBridge.App.Infrastructure;

/// <summary>
/// Small Explorer icons by file type (SHGFI_USEFILEATTRIBUTES: no disk access, no cloud download),
/// cached per extension. Must be used from the UI thread.
/// </summary>
public static class ShellIcons
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Folder => Get("<folder>", "folder", FILE_ATTRIBUTE_DIRECTORY);

    public static ImageSource? ForFile(string extension)
    {
        var ext = string.IsNullOrEmpty(extension) ? ".<none>" : extension;
        return Get(ext, "file" + (string.IsNullOrEmpty(extension) ? "" : extension), FILE_ATTRIBUTE_NORMAL);
    }

    private static ImageSource? Get(string key, string fakePath, uint attributes)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            var info = new SHFILEINFOW();
            SHGetFileInfoW(fakePath, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (info.hIcon != IntPtr.Zero)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            image = null;
        }
        Cache[key] = image;
        return image;
    }
}
