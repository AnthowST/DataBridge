using Microsoft.Win32;
using DataBridge.Printers.Model;
using DataBridge.Printers.Native;

namespace DataBridge.Printers.Inventory;

public static class PortReader
{
    public const string TcpMonitor = "Standard TCP/IP Port";
    private const string TcpPortsKey = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports";

    private static readonly HashSet<string> VirtualPorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "nul:", "PORTPROMPT:", "FILE:", "XPSPort:", "SHRFAX:", "Microsoft Document Imaging Writer Port:",
    };

    public static List<PortEntry> ReadAll()
    {
        var result = new List<PortEntry>();
        foreach (var p in Spooler.EnumPorts2())
        {
            if (string.IsNullOrEmpty(p.pPortName)) continue;
            result.Add(Build(p.pPortName, p.pMonitorName, p.pDescription));
        }
        return result;
    }

    internal static PortEntry Build(string name, string? monitor, string? description)
    {
        // PORT_INFO_2.pMonitorName may be a DLL name (TCPMON.DLL) or a localized label: the monitor that
        // actually registers the port in HKLM is authoritative.
        monitor = RegisteredMonitor(name) ?? monitor;
        var entry = new PortEntry
        {
            Name = name,
            Monitor = monitor,
            Description = description,
            Kind = Classify(name, monitor),
        };
        // The port name is never assumed to be the address: the TCP/IP configuration is read from the monitor.
        if (entry.Kind == PortKind.StandardTcpIp) ReadTcpConfiguration(entry);
        return entry;
    }

    public static PortKind Classify(string name, string? monitor)
    {
        string m = monitor ?? "";
        if (m.Equals(TcpMonitor, StringComparison.OrdinalIgnoreCase) || m.Equals("TCPMON.DLL", StringComparison.OrdinalIgnoreCase)) return PortKind.StandardTcpIp;
        if (m.Contains("WSD", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)) return PortKind.Wsd;
        if (m.Contains("USB", StringComparison.OrdinalIgnoreCase) || m.Contains("Dot4", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || name.StartsWith("DOT4", StringComparison.OrdinalIgnoreCase)) return PortKind.Usb;
        if (name.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || m.Contains("IPP", StringComparison.OrdinalIgnoreCase) || m.Contains("Internet", StringComparison.OrdinalIgnoreCase)) return PortKind.Ipp;
        if (VirtualPorts.Contains(name)) return PortKind.Virtual;
        if (m.Equals("Local Port", StringComparison.OrdinalIgnoreCase)) return PortKind.Local;
        return PortKind.Other;
    }

    private const string MonitorsKey = @"SYSTEM\CurrentControlSet\Control\Print\Monitors";

    internal static string? RegisteredMonitor(string portName)
    {
        try
        {
            using var monitors = Registry.LocalMachine.OpenSubKey(MonitorsKey);
            if (monitors == null) return null;
            foreach (var m in monitors.GetSubKeyNames())
            {
                if (portName.Contains('\\')) return null; // file / UNC "local ports" are never registry subkeys
                using var port = monitors.OpenSubKey(m + @"\Ports\" + portName);
                if (port != null) return m;
            }
        }
        catch { }
        return null;
    }

    internal static void ReadTcpConfiguration(PortEntry entry)
    {
        using var key = Registry.LocalMachine.OpenSubKey(TcpPortsKey + "\\" + entry.Name);
        if (key == null) return;
        int protocol = key.GetValue("Protocol") is int pr ? pr : 1;
        entry.Protocol = protocol == 2 ? "LPR" : "RAW";
        string? host = key.GetValue("HostName") as string;
        string? ip = key.GetValue("IPAddress") as string;
        entry.HostAddress = !string.IsNullOrWhiteSpace(host) ? host : ip;
        entry.PortNumber = key.GetValue("PortNumber") is int pn ? pn : (protocol == 2 ? 515 : 9100);
        entry.LprQueue = key.GetValue("Queue") as string;
        entry.LprByteCounting = key.GetValue("Double Spool") is int ds && ds != 0;
        entry.SnmpEnabled = key.GetValue("SNMP Enabled") is int se && se != 0;
        entry.SnmpCommunity = key.GetValue("SNMP Community") as string;
        entry.SnmpDevIndex = key.GetValue("SNMP Index") is int si ? si : 1;
    }

    public static PortEntry? ReadOne(string name)
    {
        foreach (var p in Spooler.EnumPorts2())
            if (string.Equals(p.pPortName, name, StringComparison.OrdinalIgnoreCase))
                return Build(p.pPortName!, p.pMonitorName, p.pDescription);
        return null;
    }
}
