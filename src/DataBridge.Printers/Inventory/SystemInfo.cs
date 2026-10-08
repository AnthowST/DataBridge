using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Inventory;

public static class SystemInfo
{
    public static SourceInfo Collect()
    {
        var info = new SourceInfo
        {
            ComputerName = Environment.MachineName,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            IsElevated = IsElevated(),
        };
        using (var id = WindowsIdentity.GetCurrent())
        {
            info.UserName = id.Name;
            info.UserSid = id.User?.Value;
        }
        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
        {
            info.OsBuild = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out int b) ? b : Environment.OSVersion.Version.Build;
            info.OsUbr = key?.GetValue("UBR") is int ubr ? ubr : 0;
            info.OsEdition = key?.GetValue("EditionID") as string;
            info.OsDisplayVersion = key?.GetValue("DisplayVersion") as string;
            string product = key?.GetValue("ProductName") as string ?? "Windows";
            // ProductName still says "Windows 10" on Windows 11: the build number is authoritative (>= 22000).
            if (info.OsBuild >= 22000) product = product.Replace("Windows 10", "Windows 11");
            info.OsProductName = product;
        }
        (info.DomainOrWorkgroup, info.JoinStatus) = JoinInformation();
        return info;
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static (string? name, string status) JoinInformation()
    {
        try
        {
            if (NetGetJoinInformation(null, out IntPtr buffer, out var status) == 0)
            {
                string? name = Marshal.PtrToStringUni(buffer);
                NetApiBufferFree(buffer);
                return (name, status switch
                {
                    NETSETUP_JOIN_STATUS.Domain => "Domaine",
                    NETSETUP_JOIN_STATUS.Workgroup => "Groupe de travail",
                    NETSETUP_JOIN_STATUS.Unjoined => "Non joint",
                    _ => "Inconnu",
                });
            }
        }
        catch { }
        return (null, "Inconnu");
    }

    /// <summary>"DOMAIN\user" of the interactive session this process runs in (null if unknown).</summary>
    public static string? InteractiveSessionUser()
    {
        try
        {
            string? Query(WTS_INFO_CLASS cls)
            {
                if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, cls, out IntPtr p, out _)) return null;
                try { return Marshal.PtrToStringUni(p); } finally { WTSFreeMemory(p); }
            }
            string? user = Query(WTS_INFO_CLASS.WTSUserName);
            string? domain = Query(WTS_INFO_CLASS.WTSDomainName);
            if (string.IsNullOrEmpty(user)) return null;
            return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
        }
        catch { return null; }
    }

    /// <summary>
    /// True when this process runs under the account logged on to the session. False when started with
    /// "Run as another user" or elevated with the credentials of a different administrator: per-user printers,
    /// preferences and default printer would then belong to the wrong profile.
    /// </summary>
    public static bool RunsAsSessionUser(out string processUser, out string? sessionUser)
    {
        using var id = WindowsIdentity.GetCurrent();
        processUser = id.Name;
        sessionUser = InteractiveSessionUser();
        if (sessionUser == null) return true; // cannot tell: do not block
        return string.Equals(processUser, sessionUser, StringComparison.OrdinalIgnoreCase);
    }

    public static string SpoolerState()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return "Inconnu";
        try
        {
            IntPtr svc = OpenService(scm, "Spooler", SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return "Introuvable";
            try
            {
                if (!QueryServiceStatus(svc, out var st)) return "Inconnu";
                return st.dwCurrentState == SERVICE_RUNNING ? "En cours d'exécution"
                    : st.dwCurrentState == SERVICE_STOPPED ? "Arrêté" : "En transition";
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool SpoolerRunning() => SpoolerState() == "En cours d'exécution";

    /// <summary>Starts the spooler (administrator) and waits up to 20 s.</summary>
    public static void StartSpooler()
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr svc = OpenService(scm, "Spooler", SERVICE_START | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartService(svc, 0, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1056 /* already running */) throw new System.ComponentModel.Win32Exception(err);
                }
                for (int i = 0; i < 40; i++)
                {
                    if (QueryServiceStatus(svc, out var st) && st.dwCurrentState == SERVICE_RUNNING) return;
                    Thread.Sleep(500);
                }
                throw new TimeoutException("Le spouleur n'a pas démarré dans le délai imparti.");
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static string NativeEnvironment() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "Windows x64",
        Architecture.Arm64 => "Windows ARM64",
        Architecture.X86 => "Windows NT x86",
        _ => "Windows x64",
    };

    public static int? LegacyDefaultPrinterMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows");
        return key?.GetValue("LegacyDefaultPrinterMode") is int v ? v : null;
    }

    public static void SetLegacyDefaultPrinterMode(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows");
        key.SetValue("LegacyDefaultPrinterMode", value, RegistryValueKind.DWord);
    }

    internal static string? DefaultPrinter() => Spooler.GetDefaultPrinterName();
}
