using System.Runtime.InteropServices;
using System.Text;
using DataBridge.Core.IO;
using DataBridge.Core.Native;

namespace DataBridge.Core.Detection;

public enum VolumeKind { Usb, Removable, Fixed, Network, Other }

public sealed record VolumeInfo(
    string Root,
    string Label,
    string? DeviceName,
    VolumeKind Kind,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSystem,
    int? DiskNumber)
{
    public string KindLabel => Kind switch
    {
        VolumeKind.Usb => "USB",
        VolumeKind.Removable => "Amovible",
        VolumeKind.Network => "Lecteur réseau",
        VolumeKind.Fixed => IsSystem ? "Disque système" : "Disque local",
        _ => "Volume",
    };
}

/// <summary>Result of validating a destination folder.</summary>
public sealed record DestinationCheck(
    string Path,
    bool Exists,
    bool Writable,
    long? FreeBytes,
    long? TotalBytes,
    string? FileSystem,
    string? Error)
{
    public bool IsValid => Exists && Writable && FreeBytes.HasValue;
    public bool IsFat32 => string.Equals(FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) || string.Equals(FileSystem, "FAT", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Lists volumes and checks destinations. Uses only queries allowed to a standard user.</summary>
public static class VolumeService
{
    private const uint BusTypeUsb = 7;

    public static IReadOnlyList<VolumeInfo> ListVolumes()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown or DriveType.Ram) continue;
                if (!drive.IsReady) continue;

                var root = drive.RootDirectory.FullName;
                var kind = drive.DriveType switch
                {
                    DriveType.Network => VolumeKind.Network,
                    DriveType.Removable => VolumeKind.Removable,
                    DriveType.Fixed => VolumeKind.Fixed,
                    _ => VolumeKind.Other,
                };

                string? device = null;
                int? diskNumber = null;
                if (kind != VolumeKind.Network)
                {
                    var descriptor = QueryStorageDescriptor(root);
                    if (descriptor is { } d)
                    {
                        device = d.Name;
                        if (d.BusType == BusTypeUsb) kind = VolumeKind.Usb;
                    }
                    diskNumber = QueryDiskNumber(root);
                }
                else
                {
                    device = GetNetworkTarget(root);
                }

                var label = drive.VolumeLabel;
                if (string.IsNullOrWhiteSpace(label))
                    label = kind switch
                    {
                        VolumeKind.Network => device ?? "Lecteur réseau",
                        VolumeKind.Usb or VolumeKind.Removable => device ?? "Disque amovible",
                        _ => PathUtil.AreSame(root, systemRoot) ? "Windows" : "Disque local",
                    };

                result.Add(new VolumeInfo(root, label, device, kind, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace,
                    PathUtil.AreSame(root, systemRoot), diskNumber));
            }
            catch
            {
                // A volume that cannot be queried is simply not proposed.
            }
        }
        return result;
    }

    public static (long Free, long Total)? GetFreeSpace(string path)
    {
        var dir = path.EndsWith('\\') ? path : path + "\\";
        if (NativeMethods.GetDiskFreeSpaceExW(dir, out var free, out var total, out _))
            return ((long)free, (long)total);
        return null;
    }

    public static string? GetVolumeRoot(string path)
    {
        var sb = new StringBuilder(1024);
        return NativeMethods.GetVolumePathNameW(path, sb, sb.Capacity) ? sb.ToString() : Path.GetPathRoot(path);
    }

    public static string? GetFileSystem(string path)
    {
        var root = GetVolumeRoot(path);
        if (root is null) return null;
        var fs = new StringBuilder(64);
        return NativeMethods.GetVolumeInformationW(root, null, 0, out _, out _, out _, fs, fs.Capacity) ? fs.ToString() : null;
    }

    /// <summary>Physical disk number of the volume holding <paramref name="path"/> (local volumes only).</summary>
    public static int? GetDiskNumberForPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        var root = Path.GetPathRoot(path);
        return root is null ? null : QueryDiskNumber(root);
    }

    /// <summary>Checks that the destination exists and that the current user can write there (a tiny probe file is created then deleted).</summary>
    public static DestinationCheck Check(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return new DestinationCheck(path, false, false, null, null, null, "Le dossier n'existe pas ou n'est pas accessible.");

            var probe = Path.Combine(path, $".databridge-probe-{Guid.NewGuid():N}.tmp");
            try
            {
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            }
            catch (Exception ex)
            {
                var space0 = GetFreeSpace(path);
                var isRoot = Path.GetPathRoot(path) is { } r && PathUtil.AreSame(r, path);
                var reason = ex is UnauthorizedAccessException
                    ? isRoot
                        ? $"Écriture impossible à la racine de {path} avec les droits de la session (réservée aux administrateurs). Choisissez un dossier avec « Choisir un autre dossier »."
                        : "Écriture refusée dans ce dossier avec les droits de la session. Choisissez un autre dossier."
                    : "Écriture impossible : " + ex.Message;
                return new DestinationCheck(path, true, false, space0?.Free, space0?.Total, GetFileSystem(path), reason);
            }

            var space = GetFreeSpace(path);
            return new DestinationCheck(path, true, true, space?.Free, space?.Total, GetFileSystem(path),
                space is null ? "Espace libre impossible à déterminer." : null);
        }
        catch (Exception ex)
        {
            return new DestinationCheck(path, false, false, null, null, null, ex.Message);
        }
    }

    private readonly record struct StorageDescriptor(string? Name, uint BusType);

    private static StorageDescriptor? QueryStorageDescriptor(string root)
    {
        var letter = root.TrimEnd('\\');
        using var handle = NativeMethods.CreateFileW(@"\\.\" + letter, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;

        var query = new byte[12]; // StorageDeviceProperty / PropertyStandardQuery
        var buffer = new byte[1024];
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < 32)
            return null;

        var vendorOffset = BitConverter.ToInt32(buffer, 8);
        var productOffset = BitConverter.ToInt32(buffer, 12);
        var busType = BitConverter.ToUInt32(buffer, 24);
        string? Read(int offset)
        {
            if (offset <= 0 || offset >= returned) return null;
            var end = Array.IndexOf(buffer, (byte)0, offset);
            if (end < 0) end = returned;
            var s = Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
            return s.Length == 0 ? null : s;
        }

        var vendor = Read(vendorOffset);
        var product = Read(productOffset);
        var name = string.Join(" ", new[] { vendor, product }.Where(s => !string.IsNullOrEmpty(s)));
        return new StorageDescriptor(name.Length == 0 ? null : name, busType);
    }

    private static int? QueryDiskNumber(string root)
    {
        var letter = root.TrimEnd('\\');
        if (letter.Length != 2) return null;
        using var handle = NativeMethods.CreateFileW(@"\\.\" + letter, 0, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new byte[12]; // STORAGE_DEVICE_NUMBER
        if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_GET_DEVICE_NUMBER, [], 0, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < 8)
            return null;
        return BitConverter.ToInt32(buffer, 4);
    }

    private static string? GetNetworkTarget(string root)
    {
        var sb = new StringBuilder(512);
        var len = sb.Capacity;
        return NativeMethods.WNetGetConnectionW(root.TrimEnd('\\'), sb, ref len) == 0 ? sb.ToString() : null;
    }
}
