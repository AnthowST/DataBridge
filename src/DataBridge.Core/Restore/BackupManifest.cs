using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataBridge.Core.Restore;

/// <summary>
/// databridge-manifest.json, written at the root of every backup since DataBridge 2.0. It only describes the backup
/// (what was copied, from where, to which relative folder) so that a restore does not have to guess. It never contains
/// a password, a Wi-Fi key, a token, a cookie or any file content.
/// </summary>
public sealed class BackupManifest
{
    public const string FileName = "databridge-manifest.json";
    /// <summary>Highest schema understood by this version. A backup written with a newer schema is refused cleanly.</summary>
    public const int CurrentSchemaVersion = 1;
    public const string FormatName = "DataBridge backup manifest";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Format { get; set; } = FormatName;
    public string DataBridgeVersion { get; set; } = "";
    public string BackupId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>completed / cancelled / failed.</summary>
    public string Status { get; set; } = "completed";
    public ManifestSource Source { get; set; } = new();
    public ManifestTotals Totals { get; set; } = new();
    public ManifestOptions Options { get; set; } = new();
    /// <summary>Categories present in the backup (personalFolders, additionalItems, outlookSignatures, browsers, wifi, wallpaper).</summary>
    public List<string> Categories { get; set; } = [];
    public List<ManifestPersonalFolder> PersonalFolders { get; set; } = [];
    public List<ManifestAdditionalItem> AdditionalItems { get; set; } = [];
    public ManifestSignatures? OutlookSignatures { get; set; }
    public List<ManifestBrowser> Browsers { get; set; } = [];
    public List<ManifestWifiProfile> Wifi { get; set; } = [];
    public ManifestWallpaper? Wallpaper { get; set; }
    /// <summary>2.1: printers exported to a .prnbak archive (PrintBridge format). Optional: older readers ignore it.</summary>
    public ManifestPrinters? Printers { get; set; }
    /// <summary>2.1: Outlook mailbox exported to a new PST. Optional: older readers ignore it.</summary>
    public ManifestOutlookMailbox? OutlookMailbox { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };
}

public sealed class ManifestSource
{
    public string Computer { get; set; } = "";
    public string User { get; set; } = "";
    public string? Domain { get; set; }
    public string ProfilePath { get; set; } = "";
    public string? WindowsVersion { get; set; }
}

public sealed class ManifestTotals
{
    public long Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class ManifestOptions
{
    /// <summary>OneDrive / cloud "online-only" files were not downloaded (so not in the backup).</summary>
    public bool SkipCloudOnly { get; set; } = true;
}

public sealed class ManifestPersonalFolder
{
    /// <summary>Desktop, Documents, Downloads, Pictures, Videos, Music.</summary>
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    /// <summary>local / oneDrive / network.</summary>
    public string Location { get; set; } = "local";
    public ManifestOneDrive? OneDrive { get; set; }
    /// <summary>Only part of the folder was selected.</summary>
    public bool Partial { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
}

/// <summary>OneDrive account of a source folder: type and organisation only (the e-mail stays out of the manifest).</summary>
public sealed class ManifestOneDrive
{
    public string AccountType { get; set; } = "";
    public string? Organization { get; set; }
}

public sealed class ManifestAdditionalItem
{
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsFile { get; set; }
    public long Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class ManifestSignatures
{
    public string OriginalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public long Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class ManifestBrowser
{
    /// <summary>chrome / edge / firefox.</summary>
    public string Browser { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalDataRoot { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public List<ManifestBrowserProfile> Profiles { get; set; } = [];
    public long Files { get; set; }
    public long Bytes { get; set; }
}

public sealed class ManifestBrowserProfile
{
    public string Folder { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Account shown by the browser itself (already displayed in DataBridge). Never a password or token.</summary>
    public string? Account { get; set; }
    public bool IsActive { get; set; }
    public string OriginalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
}

/// <summary>A Wi-Fi profile: its name and XML file only. The key is never copied here (only whether the XML holds it).</summary>
public sealed class ManifestWifiProfile
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
    public string? Authentication { get; set; }
    public bool KeyIncluded { get; set; }
    /// <summary>allUsers / currentUser.</summary>
    public string? Scope { get; set; }
}

public sealed class ManifestWallpaper
{
    public string OriginalPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Bytes { get; set; }
}

/// <summary>Printers archive (names, kinds and support level only; settings and drivers are inside the .prnbak).</summary>
public sealed class ManifestPrinters
{
    public string File { get; set; } = "";
    public long Bytes { get; set; }
    public List<ManifestPrinter> Printers { get; set; } = [];
}

public sealed class ManifestPrinter
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool Supported { get; set; }
    public bool IsDefault { get; set; }
}

/// <summary>Outlook mailbox: the PST and the autocomplete copy, counters only (never a mail, a contact or an address).</summary>
public sealed class ManifestOutlookMailbox
{
    public string MailboxName { get; set; } = "";
    /// <summary>Categories requested: mails, calendar, contacts, autocomplete.</summary>
    public List<string> Categories { get; set; } = [];
    public string RelativePath { get; set; } = "";
    public string? PstFile { get; set; }
    public string? AutocompleteFile { get; set; }
    public int Mails { get; set; }
    public int Contacts { get; set; }
    public int Events { get; set; }
    /// <summary>completed / partial / cancelled / failed.</summary>
    public string Status { get; set; } = "completed";
}
