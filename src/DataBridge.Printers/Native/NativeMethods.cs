using System.Runtime.InteropServices;
using System.Text;

namespace DataBridge.Printers.Native;

/// <summary>
/// P/Invoke declarations for the Windows print spooler (winspool.drv), SetupAPI, the service control manager
/// and a few helpers. Only documented Win32 functions are used.
/// </summary>
internal static class NativeMethods
{
    // EnumPrinters flags
    public const uint PRINTER_ENUM_LOCAL = 0x00000002;
    public const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    // Access rights
    public const uint PRINTER_ACCESS_ADMINISTER = 0x00000004;
    public const uint PRINTER_ACCESS_USE = 0x00000008;
    public const uint READ_CONTROL = 0x00020000;
    public const uint WRITE_DAC = 0x00040000;
    public const uint PRINTER_ALL_ACCESS = 0x000F000C;
    public const uint SERVER_ACCESS_ADMINISTER = 0x00000001;

    // PRINTER_INFO_2.Attributes
    public const uint PRINTER_ATTRIBUTE_QUEUED = 0x00000001;
    public const uint PRINTER_ATTRIBUTE_DIRECT = 0x00000002;
    public const uint PRINTER_ATTRIBUTE_DEFAULT = 0x00000004;
    public const uint PRINTER_ATTRIBUTE_SHARED = 0x00000008;
    public const uint PRINTER_ATTRIBUTE_NETWORK = 0x00000010;
    public const uint PRINTER_ATTRIBUTE_HIDDEN = 0x00000020;
    public const uint PRINTER_ATTRIBUTE_LOCAL = 0x00000040;
    public const uint PRINTER_ATTRIBUTE_ENABLE_DEVQ = 0x00000080;
    public const uint PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS = 0x00000100;
    public const uint PRINTER_ATTRIBUTE_DO_COMPLETE_FIRST = 0x00000200;
    public const uint PRINTER_ATTRIBUTE_WORK_OFFLINE = 0x00000400;
    public const uint PRINTER_ATTRIBUTE_ENABLE_BIDI = 0x00000800;
    public const uint PRINTER_ATTRIBUTE_RAW_ONLY = 0x00001000;
    public const uint PRINTER_ATTRIBUTE_PUBLISHED = 0x00002000;

    public const uint PRINTER_STATUS_OFFLINE = 0x00000080;

    // DocumentProperties modes
    public const int DM_OUT_BUFFER = 2;
    public const int DM_IN_BUFFER = 8;
    public const int IDOK = 1;

    // DeviceCapabilities
    public const ushort DC_BINS = 6;
    public const ushort DC_BINNAMES = 12;

    // AddPrinterConnection2 flags
    public const uint PRINTER_CONNECTION_NO_UI = 0x00000040;

    // UploadPrinterDriverPackage flags
    public const uint UPDP_SILENT_UPLOAD = 0x00000001;

    // Win32 errors
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_INVALID_PRINTER_NAME = 1801;
    public const int ERROR_UNKNOWN_PRINTPROCESSOR = 1798;

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_DEFAULTS
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_INFO_4
    {
        public string? pPrinterName;
        public string? pServerName;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_PTR
    {
        // PRINTER_INFO_3 (pSecurityDescriptor), PRINTER_INFO_8 and PRINTER_INFO_9 (pDevMode) all hold a single pointer.
        public IntPtr Pointer;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PORT_INFO_2
    {
        public string? pPortName;
        public string? pMonitorName;
        public string? pDescription;
        public uint fPortType;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public DateTime? ToDateTime()
        {
            long ft = ((long)dwHighDateTime << 32) | dwLowDateTime;
            if (ft <= 0) return null;
            try { return DateTime.FromFileTimeUtc(ft); } catch { return null; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DRIVER_INFO_8
    {
        public uint cVersion;
        public string? pName;
        public string? pEnvironment;
        public string? pDriverPath;
        public string? pDataFile;
        public string? pConfigFile;
        public string? pHelpFile;
        public IntPtr pDependentFiles;
        public string? pMonitorName;
        public string? pDefaultDataType;
        public IntPtr pszzPreviousNames;
        public FILETIME ftDriverDate;
        public ulong dwlDriverVersion;
        public string? pszMfgName;
        public string? pszOEMUrl;
        public string? pszHardwareID;
        public string? pszProvider;
        public string? pszPrintProcessor;
        public string? pszVendorSetup;
        public IntPtr pszzColorProfiles;
        public string? pszInfPath;
        public uint dwPrinterDriverAttributes;
        public IntPtr pszzCoreDriverDependencies;
        public FILETIME ftMinInboxDriverVerDate;
        public ulong dwlMinInboxDriverVerVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_ENUM_VALUES
    {
        public IntPtr pValueName;
        public uint cbValueName;
        public uint dwType;
        public IntPtr pData;
        public uint cbData;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PRINTER_CONNECTION_INFO_1
    {
        public uint dwFlags;
        public string? pszDriverName;
    }

    // tcpxcv.h – Standard TCP/IP port monitor, XcvData "AddPort" / "ConfigPort".
    public const int MAX_PORTNAME_LEN = 64;
    public const int MAX_NETWORKNAME_LEN = 49;
    public const int MAX_SNMP_COMMUNITY_STR_LEN = 33;
    public const int MAX_QUEUENAME_LEN = 33;
    public const int MAX_IPADDR_STR_LEN = 16;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PORT_DATA_1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PORTNAME_LEN)] public string sztPortName;
        public uint dwVersion;
        public uint dwProtocol;
        public uint cbSize;
        public uint dwReserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_NETWORKNAME_LEN)] public string sztHostAddress;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_SNMP_COMMUNITY_STR_LEN)] public string sztSNMPCommunity;
        public uint dwDoubleSpool;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_QUEUENAME_LEN)] public string sztQueue;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_IPADDR_STR_LEN)] public string sztIPAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 540)] public byte[] Reserved;
        public uint dwPortNumber;
        public uint dwSNMPEnabled;
        public uint dwSNMPDevIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DELETE_PORT_DATA_1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PORTNAME_LEN)] public string psztPortName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 98)] public byte[] Reserved;
        public uint dwVersion;
        public uint dwReserved;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string? pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrinters(uint Flags, string? Name, uint Level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetPrinter(IntPtr hPrinter, uint Level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetPrinter(IntPtr hPrinter, uint Level, IntPtr pPrinter, uint Command);

    [DllImport("winspool.drv", EntryPoint = "AddPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr AddPrinter(string? pName, uint Level, ref PRINTER_INFO_2 pPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool DeletePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EnumPortsW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPorts(string? pName, uint Level, IntPtr pPorts, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDriversW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool EnumPrinterDrivers(string? pName, string? pEnvironment, uint Level, IntPtr pDriverInfo, uint cbBuf, out uint pcbNeeded, out uint pcReturned);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DocumentProperties(IntPtr hWnd, IntPtr hPrinter, string pDeviceName, IntPtr pDevModeOutput, IntPtr pDevModeInput, int fMode);

    [DllImport("winspool.drv", EntryPoint = "DeviceCapabilitiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int DeviceCapabilities(string pDevice, string? pPort, ushort fwCapability, IntPtr pOutput, IntPtr pDevMode);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool GetDefaultPrinter(StringBuilder? pszBuffer, ref int pcchBuffer);

    [DllImport("winspool.drv", EntryPoint = "SetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDefaultPrinter(string pszPrinter);

    [DllImport("winspool.drv", EntryPoint = "AddPrinterConnection2W", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool AddPrinterConnection2(IntPtr hWnd, string pszName, uint dwLevel, ref PRINTER_CONNECTION_INFO_1 pConnectionInfo);

    [DllImport("winspool.drv", EntryPoint = "DeletePrinterConnectionW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool DeletePrinterConnection(string pName);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDataExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumPrinterDataEx(IntPtr hPrinter, string pKeyName, IntPtr pEnumValues, uint cbEnumValues, out uint pcbEnumValues, out uint pnEnumValues);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterKeyW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumPrinterKey(IntPtr hPrinter, string pKeyName, IntPtr pSubkey, uint cbSubkey, out uint pcbSubkey);

    [DllImport("winspool.drv", EntryPoint = "SetPrinterDataExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int SetPrinterDataEx(IntPtr hPrinter, string pKeyName, string pValueName, uint Type, byte[] pData, uint cbData);

    [DllImport("winspool.drv", EntryPoint = "XcvDataW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool XcvData(IntPtr hXcv, string pszDataName, IntPtr pInputData, uint cbInputData, IntPtr pOutputData, uint cbOutputData, out uint pcbOutputNeeded, out uint pdwStatus);

    [DllImport("winspool.drv", EntryPoint = "UploadPrinterDriverPackageW", CharSet = CharSet.Unicode)]
    public static extern int UploadPrinterDriverPackage(string? pszServer, string pszInfPath, string? pszEnvironment, uint dwFlags, IntPtr hwnd, StringBuilder pszDestInfPath, ref uint pcchDestInfPath);

    [DllImport("winspool.drv", EntryPoint = "InstallPrinterDriverFromPackageW", CharSet = CharSet.Unicode)]
    public static extern int InstallPrinterDriverFromPackage(string? pszServer, string? pszInfPath, string pszDriverName, string? pszEnvironment, uint dwFlags);

    [DllImport("setupapi.dll", EntryPoint = "SetupGetInfDriverStoreLocationW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetupGetInfDriverStoreLocation(string FileName, IntPtr AlternatePlatformInfo, string? LocaleName, StringBuilder ReturnBuffer, uint ReturnBufferSize, out uint RequiredSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern uint GetSecurityDescriptorLength(IntPtr pSecurityDescriptor);

    // Service control manager (spooler state / start).
    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_QUERY_STATUS = 0x0004;
    public const uint SERVICE_START = 0x0010;
    public const uint SERVICE_RUNNING = 0x00000004;
    public const uint SERVICE_STOPPED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint dwAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr hSCObject);

    // Domain / workgroup membership.
    public enum NETSETUP_JOIN_STATUS { Unknown = 0, Unjoined, Workgroup, Domain }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetGetJoinInformation(string? server, out IntPtr domain, out NETSETUP_JOIN_STATUS status);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);

    // Interactive session user (to detect "run as another user").
    public const int WTS_CURRENT_SESSION = -1;
    public enum WTS_INFO_CLASS { WTSUserName = 5, WTSDomainName = 7 }

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSQuerySessionInformation(IntPtr hServer, int sessionId, WTS_INFO_CLASS wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);
}
