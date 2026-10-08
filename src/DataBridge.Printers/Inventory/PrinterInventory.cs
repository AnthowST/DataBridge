using Microsoft.Win32;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Inventory;

/// <summary>A printer as read from the live system, with its binary settings.</summary>
public sealed class LivePrinter
{
    public PrinterEntry Entry { get; init; } = new();
    public byte[]? GlobalDevMode { get; set; }
    public byte[]? UserDevMode { get; set; }
    public byte[]? GlobalTicket { get; set; }
    public byte[]? UserTicket { get; set; }
    public List<PrinterDataValue>? DriverData { get; set; }
    public List<(short id, string name)> Bins { get; set; } = new();
    /// <summary>Component → problem met while reading (shown in the details and the export report).</summary>
    public List<ComponentStatus> ReadIssues { get; } = new();
}

public sealed class LiveInventory
{
    public List<LivePrinter> Printers { get; } = new();
    public List<PortEntry> Ports { get; set; } = new();
    public List<DriverEntry> Drivers { get; set; } = new();
    public string? DefaultPrinter { get; set; }
    public int? LegacyDefaultPrinterMode { get; set; }
    public string Environment { get; set; } = "";
}

public static class PrinterInventory
{
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Reads every printer visible to the current user (local queues and the user's connections).</summary>
    public static LiveInventory Read(bool details, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var inv = new LiveInventory { Environment = SystemInfo.NativeEnvironment() };
        progress?.Report("Lecture des ports…");
        inv.Ports = PortReader.ReadAll();
        progress?.Report("Lecture des pilotes…");
        inv.Drivers = DriverReader.ReadAll(inv.Environment, includeSigner: details);
        inv.DefaultPrinter = SystemInfo.DefaultPrinter();
        inv.LegacyDefaultPrinterMode = SystemInfo.LegacyDefaultPrinterMode();
        var machineConnections = MachineConnections();

        // Level 4 is served from the local cache: it never blocks on an unreachable print server.
        var names = new List<PRINTER_INFO_4>();
        foreach (var p in Spooler.EnumPrinters4(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS))
            if (!string.IsNullOrEmpty(p.pPrinterName) && !names.Any(n => string.Equals(n.pPrinterName, p.pPrinterName, StringComparison.OrdinalIgnoreCase)))
                names.Add(p);

        foreach (var p in names)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Lecture de « {p.pPrinterName} »…");
            inv.Printers.Add(ReadPrinter(p.pPrinterName!, p.Attributes, details, inv, machineConnections));
        }
        for (int i = 0; i < inv.Printers.Count; i++) inv.Printers[i].Entry.Id = $"P{i + 1:000}";
        return inv;
    }

    private static bool IsConnectionName(string name) => name.StartsWith(@"\\", StringComparison.Ordinal);

    public static LivePrinter ReadPrinter(string name, uint attributesHint, bool details, LiveInventory context, HashSet<string>? machineConnections = null)
    {
        if (IsConnectionName(name))
        {
            // A connection may hang while the server is unreachable: bounded wait, then fall back to the UNC path.
            var task = Task.Run(() => ReadPrinterCore(name, attributesHint, details, context, machineConnections));
            if (task.Wait(ConnectionTimeout)) return task.Result;
            var fallback = MinimalConnection(name, attributesHint, machineConnections);
            fallback.ReadIssues.Add(new ComponentStatus("", "Configuration de la file", ComponentState.Partial,
                "Serveur d'impression injoignable : seul le chemin UNC est connu (suffisant pour recréer la connexion)."));
            fallback.Entry.IsOffline = true;
            return fallback;
        }
        return ReadPrinterCore(name, attributesHint, details, context, machineConnections);
    }

    private static LivePrinter MinimalConnection(string name, uint attributes, HashSet<string>? machineConnections)
    {
        var e = new PrinterEntry { Name = name, Attributes = attributes };
        SplitUnc(name, out var server, out var share);
        e.ServerName = server;
        e.ShareName = share;
        e.Kind = machineConnections?.Contains(name) == true ? PrinterKind.MachineConnection : PrinterKind.SharedConnection;
        ApplySupport(e, null, null);
        return new LivePrinter { Entry = e };
    }

    private static LivePrinter ReadPrinterCore(string name, uint attributesHint, bool details, LiveInventory context, HashSet<string>? machineConnections)
    {
        var live = new LivePrinter { Entry = new PrinterEntry { Name = name, Attributes = attributesHint } };
        var e = live.Entry;
        bool canReadSecurity = true;
        if (!PrinterHandle.TryOpen(name, PRINTER_ACCESS_USE | READ_CONTROL, out var h, out _))
        {
            canReadSecurity = false;
            if (!PrinterHandle.TryOpen(name, PRINTER_ACCESS_USE, out h, out int err))
            {
                if (IsConnectionName(name))
                {
                    var minimal = MinimalConnection(name, attributesHint, machineConnections);
                    minimal.ReadIssues.Add(new ComponentStatus("", "Configuration de la file", ComponentState.Partial,
                        $"Connexion inaccessible ({Spooler.WindowsErrorText(err)}) : seul le chemin UNC est connu."));
                    minimal.Entry.IsOffline = true;
                    return minimal;
                }
                live.ReadIssues.Add(new ComponentStatus("", "Configuration de la file", ComponentState.Failed, Spooler.WindowsErrorText(err)));
                ApplySupport(e, null, null);
                return live;
            }
        }

        using (h)
        {
            byte[]? level2DevMode = null;
            try
            {
                var (info, dm) = Spooler.GetPrinter2(h!);
                level2DevMode = dm;
                e.ServerName = info.pServerName;
                e.ShareName = info.pShareName;
                e.PortName = info.pPortName;
                e.DriverName = info.pDriverName;
                e.Comment = info.pComment;
                e.Location = info.pLocation;
                e.SepFile = info.pSepFile;
                e.PrintProcessor = info.pPrintProcessor;
                e.Datatype = info.pDatatype;
                e.Parameters = info.pParameters;
                e.Attributes = info.Attributes;
                e.Priority = info.Priority;
                e.DefaultPriority = info.DefaultPriority;
                e.StartTime = info.StartTime;
                e.UntilTime = info.UntilTime;
                e.Status = info.Status;
                e.IsShared = (info.Attributes & PRINTER_ATTRIBUTE_SHARED) != 0;
                e.IsPublished = (info.Attributes & PRINTER_ATTRIBUTE_PUBLISHED) != 0;
                e.IsOffline = (info.Attributes & PRINTER_ATTRIBUTE_WORK_OFFLINE) != 0 || (info.Status & PRINTER_STATUS_OFFLINE) != 0;
            }
            catch (Exception ex)
            {
                live.ReadIssues.Add(new ComponentStatus("", "Configuration de la file", ComponentState.Failed, ex.Message));
            }

            bool isConnection = IsConnectionName(name);
            if (isConnection)
            {
                SplitUnc(name, out var server, out var share);
                e.ServerName ??= server;
                e.ShareName = share;
                e.IsShared = false; // the share belongs to the server, not to this PC
                e.Kind = machineConnections?.Contains(name) == true ? PrinterKind.MachineConnection : PrinterKind.SharedConnection;
            }
            var port = e.PortName == null ? null : context.Ports.FirstOrDefault(p => string.Equals(p.Name, e.PortName, StringComparison.OrdinalIgnoreCase));
            var driver = e.DriverName == null ? null : context.Drivers.FirstOrDefault(d => string.Equals(d.Name, e.DriverName, StringComparison.OrdinalIgnoreCase));
            if (!isConnection) e.Kind = Classify(e, port, driver);
            ApplySupport(e, port, driver);
            e.IsDefault = string.Equals(context.DefaultPrinter, name, StringComparison.OrdinalIgnoreCase);

            if (!details) return live;

            // Paper sources: needed to carry the default tray by name rather than by driver-specific number.
            try { live.Bins = Spooler.GetBins(name, e.PortName); } catch { }

            // Global defaults of the queue (PRINTER_INFO_8, falling back to PRINTER_INFO_2.pDevMode).
            try { live.GlobalDevMode = Spooler.GetDevModeLevel(h!, 8) ?? level2DevMode; }
            catch { live.GlobalDevMode = level2DevMode; }
            if (live.GlobalDevMode == null)
                live.ReadIssues.Add(new ComponentStatus("", "Réglages par défaut de la file", ComponentState.NotExported, "Le pilote ne fournit pas de DEVMODE."));

            // Preferences of the current user (PRINTER_INFO_9). Absent when the user never changed them.
            try { live.UserDevMode = Spooler.GetDevModeLevel(h!, 9); }
            catch (Exception ex) { live.ReadIssues.Add(new ComponentStatus("", "Préférences de l'utilisateur", ComponentState.Failed, ex.Message)); }

            if (live.GlobalDevMode != null)
            {
                try { live.GlobalTicket = Tickets.DevModeToTicketXml(name, live.GlobalDevMode); }
                catch (Exception ex) { live.ReadIssues.Add(new ComponentStatus("", "PrintTicket (défaut de la file)", ComponentState.NotExported, ex.Message)); }
            }
            if (live.UserDevMode != null)
            {
                try { live.UserTicket = Tickets.DevModeToTicketXml(name, live.UserDevMode); }
                catch (Exception ex) { live.ReadIssues.Add(new ComponentStatus("", "PrintTicket (utilisateur)", ComponentState.NotExported, ex.Message)); }
            }

            // Driver-private data: installed options (trays, duplex unit, finisher…) for most v3 drivers, Config: properties for v4.
            if (!isConnection)
            {
                try
                {
                    live.DriverData = Spooler.ReadPrinterData(h!, "PrinterDriverData")
                        .Select(v => new PrinterDataValue { Key = v.key, Name = v.name, Type = v.type, Data = Convert.ToBase64String(v.data) })
                        .ToList();
                }
                catch (Exception ex) { live.ReadIssues.Add(new ComponentStatus("", "Options et données du pilote", ComponentState.Failed, ex.Message)); }
            }

            // Permissions (only meaningful for local queues; a connection's ACL belongs to the server).
            if (!isConnection)
            {
                if (!canReadSecurity)
                    live.ReadIssues.Add(new ComponentStatus("", "Permissions", ComponentState.NotExported, "Lecture des permissions refusée pour ce compte."));
                else
                {
                    try
                    {
                        var sd = Spooler.GetSecurityDescriptor(h!);
                        if (sd != null) e.Security = SecurityReader.Analyze(sd);
                    }
                    catch (Exception ex) { live.ReadIssues.Add(new ComponentStatus("", "Permissions", ComponentState.Failed, ex.Message)); }
                }
            }
        }
        return live;
    }

    internal static void SplitUnc(string unc, out string? server, out string? share)
    {
        server = null;
        share = null;
        if (!unc.StartsWith(@"\\", StringComparison.Ordinal)) return;
        string rest = unc[2..];
        int slash = rest.IndexOf('\\');
        if (slash < 0) { server = @"\\" + rest; return; }
        server = @"\\" + rest[..slash];
        share = rest[(slash + 1)..];
    }

    public static PrinterKind Classify(PrinterEntry e, PortEntry? port, DriverEntry? driver)
    {
        string driverName = e.DriverName ?? "";
        if (driverName.Contains("Universal Print", StringComparison.OrdinalIgnoreCase) && !driverName.Contains("Universal Print Class Driver", StringComparison.Ordinal))
            return PrinterKind.UniversalPrint;
        if (driverName.Equals("Universal Print Class Driver", StringComparison.OrdinalIgnoreCase)) return PrinterKind.UniversalPrint;
        if (port != null)
        {
            switch (port.Kind)
            {
                case PortKind.StandardTcpIp:
                    return driverName.Contains("IPP Class Driver", StringComparison.OrdinalIgnoreCase) ? PrinterKind.Ipp : PrinterKind.TcpIp;
                case PortKind.Wsd: return PrinterKind.Wsd;
                case PortKind.Usb: return PrinterKind.Usb;
                case PortKind.Ipp: return PrinterKind.Ipp;
                case PortKind.Virtual: return PrinterKind.Virtual;
            }
        }
        if (driverName.Contains("IPP Class Driver", StringComparison.OrdinalIgnoreCase)) return PrinterKind.Ipp;
        if (driverName.Contains("Print To PDF", StringComparison.OrdinalIgnoreCase) || driverName.Contains("XPS Document Writer", StringComparison.OrdinalIgnoreCase)
            || driverName.Contains("OneNote", StringComparison.OrdinalIgnoreCase) || driverName.Contains("Fax", StringComparison.OrdinalIgnoreCase))
            return PrinterKind.Virtual;
        return PrinterKind.Other;
    }

    private static void ApplySupport(PrinterEntry e, PortEntry? port, DriverEntry? driver)
    {
        string env = SystemInfo.NativeEnvironment();
        switch (e.Kind)
        {
            case PrinterKind.TcpIp:
                e.Support = SupportLevel.Supported;
                break;
            case PrinterKind.SharedConnection:
                e.Support = SupportLevel.Supported;
                break;
            case PrinterKind.MachineConnection:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Connexion ajoutée pour tous les utilisateurs (printui /ga) : prise en charge prévue dans une version ultérieure.";
                break;
            case PrinterKind.Usb:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Imprimante USB : Windows la réinstalle au branchement (Plug-and-Play). Le pilote peut être exporté, la file n'est pas recréée.";
                break;
            case PrinterKind.Wsd:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Port WSD : créé par la découverte réseau de Windows, il ne peut pas être recréé à l'identique sur un autre PC.";
                break;
            case PrinterKind.Ipp:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Imprimante IPP / Mopria : prise en charge prévue dans une version ultérieure.";
                break;
            case PrinterKind.Virtual:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Imprimante virtuelle (PDF, XPS, OneNote, fax…) : fournie par Windows ou par une application, non migrée.";
                break;
            case PrinterKind.UniversalPrint:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = "Universal Print (service cloud Microsoft) : se réinstalle depuis les paramètres Windows avec le compte de l'organisation.";
                break;
            default:
                e.Support = SupportLevel.DetectedOnly;
                e.SupportNote = $"Type de port non pris en charge ({port?.Monitor ?? "inconnu"}).";
                break;
        }
        if (e.Support == SupportLevel.Supported && driver != null && !string.Equals(driver.Environment, env, StringComparison.OrdinalIgnoreCase))
        {
            e.Support = SupportLevel.DetectedOnly;
            e.SupportNote = $"Pilote {driver.Environment} : seule l'architecture {env} est prise en charge.";
        }
        if (e.Support == SupportLevel.Supported && env != "Windows x64")
        {
            e.Support = SupportLevel.DetectedOnly;
            e.SupportNote = $"Ce PC est en {env} : cette version ne prend en charge que Windows x64.";
        }
    }

    /// <summary>Per-machine connections (printui /ga) stored under HKLM.</summary>
    private static HashSet<string> MachineConnections()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Print\Connections");
            if (key == null) return set;
            foreach (var sub in key.GetSubKeyNames())
            {
                // ",,server,printer" → \\server\printer
                var parts = sub.Split(',');
                if (parts.Length >= 4) set.Add($@"\\{parts[2]}\{string.Join(",", parts.Skip(3))}");
            }
        }
        catch { }
        return set;
    }
}
