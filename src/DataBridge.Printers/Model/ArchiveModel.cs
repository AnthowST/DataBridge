using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataBridge.Printers.Model;

public enum PrinterKind
{
    /// <summary>Local queue on a Standard TCP/IP port (RAW or LPR).</summary>
    TcpIp,
    /// <summary>Per-user connection to a queue shared by a print server (\\server\printer).</summary>
    SharedConnection,
    /// <summary>Per-machine connection (printui /ga).</summary>
    MachineConnection,
    Usb,
    Wsd,
    Ipp,
    Virtual,
    UniversalPrint,
    Other,
}

public enum SupportLevel { Supported, DetectedOnly }

public enum PortKind { StandardTcpIp, Wsd, Usb, Local, Ipp, Virtual, Other }

public enum ComponentState { Exported, Partial, NotExported, NotApplicable, Failed }

public sealed class ComponentStatus
{
    public string Target { get; set; } = "";
    public string Component { get; set; } = "";
    public ComponentState State { get; set; }
    public string? Detail { get; set; }

    public ComponentStatus() { }
    public ComponentStatus(string target, string component, ComponentState state, string? detail = null)
    {
        Target = target;
        Component = component;
        State = state;
        Detail = detail;
    }
}

/// <summary>Documented public DEVMODE fields. Null = field not set by the driver.</summary>
public sealed class DevModeSummary
{
    public short? Orientation { get; set; }
    public short? PaperSize { get; set; }
    public string? FormName { get; set; }
    public short? PaperLength { get; set; }
    public short? PaperWidth { get; set; }
    public short? Scale { get; set; }
    public short? Copies { get; set; }
    public short? DefaultSource { get; set; }
    public string? DefaultSourceName { get; set; }
    public short? PrintQuality { get; set; }
    public short? YResolution { get; set; }
    public short? Color { get; set; }
    public short? Duplex { get; set; }
    public short? Collate { get; set; }
    public int? Nup { get; set; }
    public int? MediaType { get; set; }

    public ushort DriverVersionField { get; set; }
    public ushort DriverExtra { get; set; }
    public ushort Size { get; set; }
}

/// <summary>One layer of print settings (global defaults of the queue, or preferences of the current user).</summary>
public sealed class SettingsLayer
{
    public string? DevModeFile { get; set; }
    public string? PrintTicketFile { get; set; }
    public DevModeSummary? Summary { get; set; }
    public bool PrivatePartExcluded { get; set; }
    /// <summary>For the user layer: true when the user's preferences differ from the queue defaults.</summary>
    public bool DiffersFromGlobal { get; set; }
}

public sealed class PrinterDataValue
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public uint Type { get; set; }
    public string Data { get; set; } = ""; // base64
}

public sealed class DriverDataInfo
{
    public string File { get; set; } = "";
    public int ValueCount { get; set; }
}

public enum SidPortability { WellKnown, Domain, LocalAccount, Unresolved }

public sealed class AceInfo
{
    public string Sid { get; set; } = "";
    public string? Account { get; set; }
    public bool IsDeny { get; set; }
    public int AccessMask { get; set; }
    public byte AceFlags { get; set; }
    public SidPortability Portability { get; set; }
}

public sealed class SecurityInfo
{
    public string Sddl { get; set; } = "";
    public List<AceInfo> Aces { get; set; } = new();
    public bool HasNonPortableEntries { get; set; }
    public bool HasUnsupportedAces { get; set; }
}

public sealed class PrinterEntry
{
    public string Id { get; set; } = "";
    /// <summary>Exact name as shown by Windows (accents, spaces and any character preserved).</summary>
    public string Name { get; set; } = "";
    public PrinterKind Kind { get; set; }
    public SupportLevel Support { get; set; }
    public string? SupportNote { get; set; }

    public string? ServerName { get; set; }
    public string? ShareName { get; set; }
    public bool IsShared { get; set; }
    public bool IsPublished { get; set; }

    public string? PortName { get; set; }
    public string? PortId { get; set; }
    public string? DriverName { get; set; }
    public string? DriverId { get; set; }

    public string? Comment { get; set; }
    public string? Location { get; set; }
    public string? PrintProcessor { get; set; }
    public string? Datatype { get; set; }
    public string? SepFile { get; set; }
    public string? Parameters { get; set; }
    public uint Attributes { get; set; }
    public uint Priority { get; set; }
    public uint DefaultPriority { get; set; }
    public uint StartTime { get; set; }
    public uint UntilTime { get; set; }
    public uint Status { get; set; }

    public bool IsDefault { get; set; }
    public bool IsOffline { get; set; }

    public SettingsLayer? GlobalSettings { get; set; }
    public SettingsLayer? UserSettings { get; set; }
    public DriverDataInfo? DriverData { get; set; }
    public SecurityInfo? Security { get; set; }

    public List<ComponentStatus> Components { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public sealed class PortEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Monitor { get; set; }
    public string? Description { get; set; }
    public PortKind Kind { get; set; }
    /// <summary>"RAW" or "LPR" for Standard TCP/IP ports.</summary>
    public string? Protocol { get; set; }
    public string? HostAddress { get; set; }
    public int PortNumber { get; set; }
    public string? LprQueue { get; set; }
    public bool LprByteCounting { get; set; }
    public bool SnmpEnabled { get; set; }
    public string? SnmpCommunity { get; set; }
    public int SnmpDevIndex { get; set; }

    public bool SameConfigurationAs(PortEntry other) =>
        string.Equals(Protocol, other.Protocol, StringComparison.OrdinalIgnoreCase)
        && string.Equals(HostAddress, other.HostAddress, StringComparison.OrdinalIgnoreCase)
        && PortNumber == other.PortNumber
        && (Protocol != "LPR" || (string.Equals(LprQueue ?? "", other.LprQueue ?? "", StringComparison.Ordinal) && LprByteCounting == other.LprByteCounting))
        && SnmpEnabled == other.SnmpEnabled
        && (!SnmpEnabled || (SnmpDevIndex == other.SnmpDevIndex && string.Equals(SnmpCommunity ?? "", other.SnmpCommunity ?? "", StringComparison.Ordinal)));

    public string Describe() => Kind == PortKind.StandardTcpIp
        ? $"{Protocol} {HostAddress}:{PortNumber}" + (Protocol == "LPR" ? $" file « {LprQueue} »{(LprByteCounting ? ", comptage d'octets" : "")}" : "")
          + (SnmpEnabled ? $", SNMP (index {SnmpDevIndex})" : ", SNMP désactivé")
        : $"{Kind} ({Monitor})";
}

public sealed class DriverEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Environment { get; set; } = "";
    public uint MajorVersion { get; set; }
    public string? Manufacturer { get; set; }
    public string? Provider { get; set; }
    public string Version { get; set; } = "";
    public ulong VersionRaw { get; set; }
    public DateTime? DriverDate { get; set; }
    public string? HardwareId { get; set; }
    public string? PrintProcessor { get; set; }
    public string? InfPath { get; set; }
    public string? InfOriginalName { get; set; }
    public string? PublishedInf { get; set; }
    public string? Signer { get; set; }
    public bool IsInbox { get; set; }
    public bool Exported { get; set; }
    public string? PackageFolder { get; set; }
    /// <summary>INF path inside the archive (relative, forward slashes).</summary>
    public string? PackageInf { get; set; }
    public List<string> InfArchitectures { get; set; } = new();
    public string? NotExportedReason { get; set; }
    public int UsedByCount { get; set; }
}

public sealed class SourceInfo
{
    public string ComputerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? UserSid { get; set; }
    public string OsProductName { get; set; } = "";
    public string? OsDisplayVersion { get; set; }
    public int OsBuild { get; set; }
    public int OsUbr { get; set; }
    public string? OsEdition { get; set; }
    public string Architecture { get; set; } = "";
    public string? DomainOrWorkgroup { get; set; }
    public string JoinStatus { get; set; } = "";
    public bool IsElevated { get; set; }
}

public sealed class InventoryDocument
{
    public List<PrinterEntry> Printers { get; set; } = new();
    public List<PortEntry> Ports { get; set; } = new();
    public List<DriverEntry> Drivers { get; set; } = new();
    public string? DefaultPrinter { get; set; }
    /// <summary>HKCU LegacyDefaultPrinterMode: 1 = the user manages the default printer, 0 = Windows manages it.</summary>
    public int? LegacyDefaultPrinterMode { get; set; }
}

public sealed class Manifest
{
    // Same archive format as PrintBridge: a .prnbak written by DataBridge can also be opened with PrintBridge.
    public const string FormatName = "PrintBridge.Archive";
    public const int CurrentMajor = 1;

    public string Format { get; set; } = FormatName;
    public string FormatVersion { get; set; } = "1.0";
    public string ToolVersion { get; set; } = "";
    public DateTimeOffset ExportedAt { get; set; }
    public SourceInfo Source { get; set; } = new();
    public string InventoryFile { get; set; } = "inventory.json";
    public bool PrivateDriverDataIncluded { get; set; }
    /// <summary>Relative path (forward slashes) → SHA-256 (hex, lowercase). Covers every file except manifest.json.</summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    public List<ComponentStatus> Components { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Same settings, one line per document (pipe protocol, JSON-lines journal).</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
