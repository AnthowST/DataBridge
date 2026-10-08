using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using DataBridge.Printers.Util;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Machine;

public sealed class OpResult
{
    public bool Ok { get; set; }
    public string? Message { get; set; }
    public string? Value { get; set; }

    public static OpResult Success(string? message = null, string? value = null) => new() { Ok = true, Message = message, Value = value };
    public static OpResult Fail(string message) => new() { Ok = false, Message = message };
}

public sealed class DriverPackageRequest
{
    /// <summary>Folder holding the package (extracted archive or a folder chosen by the user).</summary>
    public string PackageDir { get; set; } = "";
    /// <summary>INF path relative to PackageDir.</summary>
    public string InfRelative { get; set; } = "";
    /// <summary>Every file of the package, relative to PackageDir (forward slashes) → SHA-256.</summary>
    public Dictionary<string, string> Hashes { get; set; } = new();
    public string Environment { get; set; } = "Windows x64";
}

public sealed class PrinterCreateRequest
{
    public string Name { get; set; } = "";
    public string PortName { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string? PrintProcessor { get; set; }
    public string? Datatype { get; set; }
    public string? Comment { get; set; }
    public string? Location { get; set; }
    public string? ShareName { get; set; }
    public bool Shared { get; set; }
    public uint Attributes { get; set; }
    public uint Priority { get; set; }
    public uint DefaultPriority { get; set; }
    public uint StartTime { get; set; }
    public uint UntilTime { get; set; }
    public string? SepFile { get; set; }
    public string? Parameters { get; set; }
}

/// <summary>
/// Machine-scope operations (administrator). Implemented in-process by <see cref="MachineOps"/> and, for a
/// non-elevated UI, by the elevated worker behind a named pipe. The list is closed: no arbitrary command.
/// </summary>
public interface IMachineOps : IDisposable
{
    OpResult StartSpooler();
    OpResult StageDriverPackage(DriverPackageRequest request);
    OpResult InstallDriver(string driverName, string environment, string? storeInfPath);
    OpResult AddTcpPort(PortEntry port, string portName);
    OpResult ConfigureTcpPort(PortEntry port, string portName);
    OpResult DeleteTcpPort(string portName);
    OpResult AddPrinter(PrinterCreateRequest request);
    OpResult DeletePrinter(string name);
    OpResult SetDriverData(string printerName, List<PrinterDataValue> values);
    OpResult SetGlobalDevMode(string printerName, byte[] devMode);
    OpResult SetSecurity(string printerName, string sddl);
}

public sealed class MachineOps : IMachineOps
{
    /// <summary>Attributes copied from the source queue (shared / published / offline / network are handled separately or never copied).</summary>
    public const uint RestorableAttributes = PRINTER_ATTRIBUTE_QUEUED | PRINTER_ATTRIBUTE_DIRECT | PRINTER_ATTRIBUTE_ENABLE_DEVQ
        | PRINTER_ATTRIBUTE_KEEPPRINTEDJOBS | PRINTER_ATTRIBUTE_DO_COMPLETE_FIRST | PRINTER_ATTRIBUTE_ENABLE_BIDI | PRINTER_ATTRIBUTE_RAW_ONLY
        | PRINTER_ATTRIBUTE_HIDDEN;

    public OpResult StartSpooler() => Guard(() =>
    {
        SystemInfo.StartSpooler();
        return OpResult.Success("Spouleur démarré.");
    });

    public OpResult StageDriverPackage(DriverPackageRequest request) => Guard(() =>
    {
        // The package is copied into a folder only administrators can write, then every file is checked against
        // the expected hashes *after* the copy: a file swapped in the user-writable source cannot reach the installation.
        string staging = CreateProtectedStaging();
        try
        {
            string srcRoot = Path.GetFullPath(request.PackageDir);
            foreach (var (rel, hash) in request.Hashes)
            {
                if (Archive.ArchiveReader.ValidateEntryName(rel) != null) return OpResult.Fail($"Chemin refusé dans le package : {rel}");
                string src = Path.Combine(srcRoot, rel.Replace('/', '\\'));
                string dst = Path.Combine(staging, rel.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
                if (!string.Equals(Hashing.Sha256File(dst), hash, StringComparison.OrdinalIgnoreCase))
                    return OpResult.Fail($"Le fichier « {rel} » du package a changé depuis sa vérification : installation annulée.");
            }
            string inf = Path.Combine(staging, request.InfRelative.Replace('/', '\\'));
            if (!File.Exists(inf)) return OpResult.Fail("INF introuvable dans le package.");

            var sb = new StringBuilder(1024);
            uint len = (uint)sb.Capacity;
            int hr = UploadPrinterDriverPackage(null, inf, request.Environment, UPDP_SILENT_UPLOAD, IntPtr.Zero, sb, ref len);
            if (hr == 0 && sb.Length > 0)
                return OpResult.Success("Package ajouté au magasin de pilotes de Windows (signature vérifiée par Windows).", sb.ToString());

            // Fallback: pnputil /add-driver (same driver store, same signature checks).
            var r = PnpUtil.Run("/add-driver", inf);
            if (r.ExitCode != 0 && r.ExitCode != 259 /* already present / nothing to install */)
                return OpResult.Fail($"Windows a refusé le package (UploadPrinterDriverPackage : {Spooler.HResultText(hr)} ; pnputil code {r.ExitCode}) : {r.Output}");
            string infHash = Hashing.Sha256File(inf);
            foreach (var storeInf in DriverReader.PublishedInfMap().Keys)
            {
                if (!string.Equals(Path.GetFileName(storeInf), Path.GetFileName(inf), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.Exists(storeInf) && Hashing.Sha256File(storeInf) == infHash)
                        return OpResult.Success("Package ajouté au magasin de pilotes (pnputil).", storeInf);
                }
                catch { }
            }
            return OpResult.Fail("Package ajouté par pnputil mais introuvable dans le magasin de pilotes.");
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }
    });

    public OpResult InstallDriver(string driverName, string environment, string? storeInfPath) => Guard(() =>
    {
        int hr = InstallPrinterDriverFromPackage(null, storeInfPath, driverName, environment, 0);
        if (hr != 0)
            return OpResult.Fail(storeInfPath == null
                ? $"Le pilote intégré « {driverName} » n'est pas disponible sur ce PC : {Spooler.HResultText(hr)}"
                : $"Le package ne permet pas d'installer le pilote « {driverName} » : {Spooler.HResultText(hr)}");
        var installed = DriverReader.Find(driverName, environment);
        return installed == null
            ? OpResult.Fail($"Le pilote « {driverName} » n'apparaît pas après l'installation.")
            : OpResult.Success($"Pilote « {driverName} » {installed.Version} enregistré auprès du spouleur.", installed.Version);
    });

    public OpResult AddTcpPort(PortEntry port, string portName) => Guard(() => TcpXcv("AddPort", ",XcvMonitor " + PortReader.TcpMonitor, port, portName));

    public OpResult ConfigureTcpPort(PortEntry port, string portName) => Guard(() => TcpXcv("ConfigPort", ",XcvPort " + portName, port, portName));

    public OpResult DeleteTcpPort(string portName) => Guard(() =>
    {
        using var h = PrinterHandle.Open(",XcvMonitor " + PortReader.TcpMonitor, SERVER_ACCESS_ADMINISTER);
        var data = new DELETE_PORT_DATA_1 { psztPortName = portName, Reserved = new byte[98], dwVersion = 1 };
        return CallXcv(h, "DeletePort", data, $"Port « {portName} » supprimé.");
    });

    private static OpResult TcpXcv(string command, string xcvName, PortEntry port, string portName)
    {
        if (port.Kind != PortKind.StandardTcpIp) return OpResult.Fail("Seuls les ports TCP/IP standard peuvent être recréés.");
        if (string.IsNullOrWhiteSpace(port.HostAddress)) return OpResult.Fail("Adresse de l'imprimante absente.");
        if (portName.Length >= MAX_PORTNAME_LEN) return OpResult.Fail("Nom de port trop long (63 caractères au plus).");
        if (port.HostAddress.Length >= MAX_NETWORKNAME_LEN) return OpResult.Fail($"Nom d'hôte trop long pour le moniteur TCP/IP ({MAX_NETWORKNAME_LEN - 1} caractères au plus).");
        if ((port.LprQueue?.Length ?? 0) >= MAX_QUEUENAME_LEN) return OpResult.Fail("Nom de file LPR trop long.");
        if ((port.SnmpCommunity?.Length ?? 0) >= MAX_SNMP_COMMUNITY_STR_LEN) return OpResult.Fail("Communauté SNMP trop longue.");

        var data = new PORT_DATA_1
        {
            sztPortName = portName,
            dwVersion = 1,
            dwProtocol = port.Protocol == "LPR" ? 2u : 1u,
            cbSize = (uint)Marshal.SizeOf<PORT_DATA_1>(),
            sztHostAddress = port.HostAddress,
            sztSNMPCommunity = port.SnmpCommunity ?? "public",
            dwDoubleSpool = port.LprByteCounting ? 1u : 0u,
            sztQueue = port.LprQueue ?? "",
            sztIPAddress = "",
            Reserved = new byte[540],
            dwPortNumber = (uint)port.PortNumber,
            dwSNMPEnabled = port.SnmpEnabled ? 1u : 0u,
            dwSNMPDevIndex = (uint)Math.Max(1, port.SnmpDevIndex),
        };
        using var h = PrinterHandle.Open(xcvName, SERVER_ACCESS_ADMINISTER);
        return CallXcv(h, command, data, command == "AddPort" ? $"Port « {portName} » créé ({port.Describe()})." : $"Port « {portName} » reconfiguré ({port.Describe()}).");
    }

    private static OpResult CallXcv<T>(PrinterHandle h, string command, T data, string success) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(data, buf, false);
            if (!XcvData(h.Handle, command, buf, (uint)size, IntPtr.Zero, 0, out _, out uint status))
            {
                int err = Marshal.GetLastWin32Error();
                return OpResult.Fail($"{command} : {new Win32Exception(err).Message}");
            }
            if (status != 0) return OpResult.Fail($"{command} : {new Win32Exception((int)status).Message}");
            return OpResult.Success(success);
        }
        finally
        {
            Marshal.DestroyStructure<T>(buf);
            Marshal.FreeHGlobal(buf);
        }
    }

    public OpResult AddPrinter(PrinterCreateRequest r) => Guard(() =>
    {
        var info = new PRINTER_INFO_2
        {
            pPrinterName = r.Name,
            pPortName = r.PortName,
            pDriverName = r.DriverName,
            pPrintProcessor = string.IsNullOrEmpty(r.PrintProcessor) ? "winprint" : r.PrintProcessor,
            pDatatype = string.IsNullOrEmpty(r.Datatype) ? "RAW" : r.Datatype,
            pComment = r.Comment,
            pLocation = r.Location,
            pShareName = r.Shared ? r.ShareName : null,
            pSepFile = r.SepFile,
            pParameters = r.Parameters,
            Attributes = (r.Attributes & RestorableAttributes) | (r.Shared ? PRINTER_ATTRIBUTE_SHARED : 0),
            Priority = r.Priority == 0 ? 1 : r.Priority,
            DefaultPriority = r.DefaultPriority,
            StartTime = r.StartTime,
            UntilTime = r.UntilTime,
        };
        string? note = null;
        IntPtr h = NativeMethods.AddPrinter(null, 2, ref info);
        if (h == IntPtr.Zero && Marshal.GetLastWin32Error() == ERROR_UNKNOWN_PRINTPROCESSOR)
        {
            // The source print processor is not installed here: use the one declared by the driver, and say so.
            var drv = DriverReader.Find(r.DriverName, SystemInfo.NativeEnvironment());
            string fallback = drv?.PrintProcessor ?? "winprint";
            note = $" Processeur d'impression « {info.pPrintProcessor} » absent : « {fallback} » (déclaré par le pilote) utilisé.";
            info.pPrintProcessor = fallback;
            h = NativeMethods.AddPrinter(null, 2, ref info);
        }
        if (h == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return OpResult.Fail($"Création de « {r.Name} » refusée : {new Win32Exception(err).Message}");
        }
        ClosePrinter(h);
        return OpResult.Success($"Imprimante « {r.Name} » créée." + note);
    });

    public OpResult DeletePrinter(string name) => Guard(() =>
    {
        using var h = PrinterHandle.Open(name, PRINTER_ALL_ACCESS);
        if (!NativeMethods.DeletePrinter(h.Handle))
            return OpResult.Fail($"Suppression de « {name} » refusée : {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return OpResult.Success($"Imprimante « {name} » supprimée.");
    });

    public OpResult SetDriverData(string printerName, List<PrinterDataValue> values) => Guard(() =>
    {
        using var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_ADMINISTER);
        int ok = 0;
        var failed = new List<string>();
        foreach (var v in values)
        {
            byte[] data = Convert.FromBase64String(v.Data);
            int rc = SetPrinterDataEx(h.Handle, v.Key, v.Name, v.Type, data, (uint)data.Length);
            if (rc == 0) ok++;
            else failed.Add($"{v.Key}\\{v.Name} ({new Win32Exception(rc).Message})");
        }
        return failed.Count == 0
            ? OpResult.Success($"{ok} valeur(s) de données du pilote écrite(s).")
            : new OpResult { Ok = ok > 0, Message = $"{ok} valeur(s) écrite(s), {failed.Count} refusée(s) : {string.Join(" ; ", failed.Take(10))}" };
    });

    public OpResult SetGlobalDevMode(string printerName, byte[] devMode) => Guard(() =>
    {
        using var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_ADMINISTER);
        Spooler.SetDevModeLevel(h, 8, devMode);
        return OpResult.Success("Réglages par défaut de la file écrits.");
    });

    public OpResult SetSecurity(string printerName, string sddl) => Guard(() =>
    {
        var sd = new RawSecurityDescriptor(sddl);
        var bytes = new byte[sd.BinaryLength];
        sd.GetBinaryForm(bytes, 0);
        using var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_ADMINISTER | READ_CONTROL | WRITE_DAC);
        Spooler.SetSecurityDescriptor(h, bytes);
        return OpResult.Success("Permissions appliquées.");
    });

    private static string CreateProtectedStaging()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DataBridge", "printers-staging");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists) rootInfo = FileSystemAclExtensions.CreateDirectory(security, root);
        else rootInfo.SetAccessControl(security);
        string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static OpResult Guard(Func<OpResult> action)
    {
        try { return action(); }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    public void Dispose() { }
}
