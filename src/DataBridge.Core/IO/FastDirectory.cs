using System.ComponentModel;
using System.Runtime.InteropServices;
using DataBridge.Core.Native;

namespace DataBridge.Core.IO;

/// <summary>One directory entry as returned by FindFirstFileEx (no extra disk access, no cloud hydration).</summary>
public sealed class FsEntry
{
    // Attributes not exposed by System.IO.FileAttributes.
    public const uint AttrRecallOnOpen = 0x00040000;
    public const uint AttrRecallOnDataAccess = 0x00400000;
    public const uint AttrOffline = 0x00001000;
    private const uint NameSurrogateBit = 0x20000000;

    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required uint RawAttributes { get; init; }
    public required long Size { get; init; }
    public required uint ReparseTag { get; init; }
    public required DateTime LastWriteUtc { get; init; }

    public FileAttributes Attributes => (FileAttributes)RawAttributes;
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>
    /// Symbolic link or junction (name-surrogate reparse point), e.g. the legacy "Ma musique" junction in Documents.
    /// Never followed: it would duplicate data or loop. Cloud placeholders (OneDrive, iCloud) are NOT links.
    /// </summary>
    public bool IsLink => (Attributes & FileAttributes.ReparsePoint) != 0 && (ReparseTag & NameSurrogateBit) != 0;

    /// <summary>File whose content is not on the disk (OneDrive/iCloud "available online only").</summary>
    public bool IsCloudOnly => !IsDirectory && (RawAttributes & (AttrRecallOnDataAccess | AttrRecallOnOpen | AttrOffline)) != 0;

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name);
}

/// <summary>Win32 error raised by the file-system helpers, with the original error code kept.</summary>
public sealed class FsException : IOException
{
    public FsException(int win32Error, string path)
        : base($"{new Win32Exception(win32Error).Message} ({path})")
    {
        Win32Error = win32Error;
        Path = path;
    }

    public int Win32Error { get; }
    public string Path { get; }
}

/// <summary>Streaming directory enumeration based on FindFirstFileExW (large fetch, long-path aware).</summary>
public static class FastDirectory
{
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_NO_MORE_FILES = 18;

    /// <summary>Enumerates the immediate children of <paramref name="directory"/>. Throws <see cref="FsException"/> when the directory cannot be opened.</summary>
    public static IEnumerable<FsEntry> Enumerate(string directory)
    {
        var search = PathUtil.ToExtended(directory).TrimEnd('\\') + @"\*";
        var handle = NativeMethods.FindFirstFileExW(search, NativeMethods.FindExInfoBasic, out var data,
            NativeMethods.FindExSearchNameMatch, IntPtr.Zero, NativeMethods.FIND_FIRST_EX_LARGE_FETCH);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (err == ERROR_FILE_NOT_FOUND) yield break; // empty volume root
            throw new FsException(err, directory);
        }

        using (handle)
        {
            var prefix = directory.EndsWith('\\') ? directory : directory + "\\";
            while (true)
            {
                var name = data.cFileName;
                if (name != "." && name != "..")
                {
                    yield return new FsEntry
                    {
                        Name = name,
                        FullPath = prefix + name,
                        RawAttributes = data.dwFileAttributes,
                        Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                        ReparseTag = (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0,
                        LastWriteUtc = ToDate(data.ftLastWriteTime),
                    };
                }

                if (!NativeMethods.FindNextFileW(handle, out data))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_NO_MORE_FILES) yield break;
                    throw new FsException(err, directory);
                }
            }
        }
    }

    private static DateTime ToDate(long fileTime) =>
        fileTime > 0 && fileTime < DateTime.MaxValue.ToFileTimeUtc() ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;

    /// <summary>Reads a full listing, or returns null with the error code when the directory is not readable.</summary>
    public static List<FsEntry>? TryList(string directory, out int error)
    {
        error = 0;
        try
        {
            return Enumerate(directory).ToList();
        }
        catch (FsException ex)
        {
            error = ex.Win32Error;
            return null;
        }
    }

    /// <summary>Info for a single file or directory path (null when it does not exist / is not accessible).</summary>
    public static FsEntry? GetEntry(string path)
    {
        var parent = PathUtil.Parent(path);
        if (parent is null)
        {
            // Volume root: synthesize a directory entry.
            return Directory.Exists(path)
                ? new FsEntry { Name = path, FullPath = path, RawAttributes = (uint)FileAttributes.Directory, Size = 0, ReparseTag = 0, LastWriteUtc = DateTime.MinValue }
                : null;
        }

        var handle = NativeMethods.FindFirstFileExW(PathUtil.ToExtended(path), NativeMethods.FindExInfoBasic, out var data,
            NativeMethods.FindExSearchNameMatch, IntPtr.Zero, 0);
        using (handle)
        {
            if (handle.IsInvalid) return null;
            return new FsEntry
            {
                Name = data.cFileName,
                FullPath = path,
                RawAttributes = data.dwFileAttributes,
                Size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow,
                ReparseTag = (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : 0,
                LastWriteUtc = ToDate(data.ftLastWriteTime),
            };
        }
    }
}
