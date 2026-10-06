using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DataBridge.Core.Native;

/// <summary>Win32 declarations. Everything here works with standard user rights.</summary>
internal static class NativeMethods
{
    // ---------------------------------------------------------------- Known folders

    internal const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    // ---------------------------------------------------------------- File enumeration

    // FILETIME is two DWORDs (4-byte aligned): Pack = 4 keeps the native layout with long fields.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    internal struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    internal const int FindExInfoBasic = 1;
    internal const int FindExSearchNameMatch = 0;
    internal const int FIND_FIRST_EX_LARGE_FETCH = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern SafeFindHandle FindFirstFileExW(
        string lpFileName, int fInfoLevelId, out WIN32_FIND_DATAW lpFindFileData,
        int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextFileW(SafeFindHandle hFindFile, out WIN32_FIND_DATAW lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindClose(IntPtr hFindFile);

    internal sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    // ---------------------------------------------------------------- Copy

    internal const uint COPY_FILE_FAIL_IF_EXISTS = 0x00000001;
    internal const uint COPY_FILE_ALLOW_DECRYPTED_DESTINATION = 0x00000008;

    internal const uint PROGRESS_CONTINUE = 0;
    internal const uint PROGRESS_CANCEL = 1;

    internal delegate uint CopyProgressRoutine(
        long totalFileSize, long totalBytesTransferred, long streamSize, long streamBytesTransferred,
        uint dwStreamNumber, uint dwCallbackReason, IntPtr hSourceFile, IntPtr hDestinationFile, IntPtr lpData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CopyFileExW(
        string lpExistingFileName, string lpNewFileName, CopyProgressRoutine? lpProgressRoutine,
        IntPtr lpData, ref int pbCancel, uint dwCopyFlags);

    // ---------------------------------------------------------------- Volumes

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(
        string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformationW(
        string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize, out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength, out uint lpFileSystemFlags, StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int WNetGetConnectionW(string lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

    internal const uint FILE_SHARE_READ = 0x1;
    internal const uint FILE_SHARE_WRITE = 0x2;
    internal const uint OPEN_EXISTING = 3;
    internal const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    internal const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    internal const uint FILE_SHARE_DELETE = 0x4;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // required to open a directory handle (no privilege used)

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int GetFinalPathNameByHandleW(SafeFileHandle hFile, StringBuilder lpszFilePath, int cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    // ---------------------------------------------------------------- Token

    internal const uint TOKEN_QUERY = 0x0008;
    internal const int TokenElevationType = 18;
    internal const int TokenElevation = 20;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out SafeAccessTokenHandle tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle, int tokenInformationClass, out int tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    // ---------------------------------------------------------------- WLAN

    internal const uint WLAN_CLIENT_VERSION_2 = 2;
    internal const uint WLAN_PROFILE_GROUP_POLICY = 0x00000001;
    internal const uint WLAN_PROFILE_USER = 0x00000002;
    internal const uint WLAN_PROFILE_GET_PLAINTEXT_KEY = 0x00000004;

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    internal static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanGetProfile(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, string strProfileName, IntPtr pReserved,
        out IntPtr pstrProfileXml, ref uint pdwFlags, out uint pdwGrantedAccess);

    [DllImport("wlanapi.dll")]
    internal static extern void WlanFreeMemory(IntPtr pMemory);

    /// <summary>Imports a profile XML (restore). Windows decides whether the current, non-elevated user may add it.</summary>
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanSetProfile(
        IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, string strProfileXml, string? strAllUserProfileSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool bOverwrite, IntPtr pReserved, out uint pdwReasonCode);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanDeleteProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid, string strProfileName, IntPtr pReserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    internal static extern uint WlanReasonCodeToString(uint dwReasonCode, uint dwBufferSize, StringBuilder pStringBuffer, IntPtr pReserved);

    // ---------------------------------------------------------------- Wallpaper

    internal const uint SPI_SETDESKWALLPAPER = 0x0014;
    internal const uint SPIF_UPDATEINIFILE = 0x01;
    internal const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}
