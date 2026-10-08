using DataBridge.Core.Detection;
using DataBridge.Core.Profile;

namespace DataBridge.Core.Restore;

/// <summary>How a backup was described: by its databridge-manifest.json (2.0+) or reconstructed from its structure and copy-log.txt (1.x).</summary>
public enum BackupGeneration { Manifest, Legacy }

/// <summary>Where the backup comes from (what the manifest or the log says; values may be missing for very old backups).</summary>
public sealed record BackupSourceInfo(
    string? Computer,
    string? User,
    string? ProfilePath,
    string? WindowsVersion,
    DateTime? CreatedAt,
    string? DataBridgeVersion,
    string? Product,
    string? Status);

public sealed record BackupPersonalFolder(
    PersonalFolderKind Kind,
    string Label,
    string RelativePath,
    string FullPath,
    string? OriginalPath,
    bool Partial,
    long? Files,
    long? Bytes,
    string? SourceLocation);

public sealed record BackupAdditionalItem(
    string Name,
    string RelativePath,
    string FullPath,
    bool IsFile,
    string? OriginalPath,
    long? Files,
    long? Bytes);

public sealed record BackupSignatures(string RelativePath, string FullPath, string? OriginalPath, IReadOnlyList<string> Names);

public sealed record BackupBrowserProfile(
    string Folder,
    string DisplayName,
    string? Account,
    bool IsActive,
    string RelativePath,
    string FullPath);

public sealed record BackupBrowser(BrowserKind Kind, string Name, string RelativePath, string FullPath, IReadOnlyList<BackupBrowserProfile> Profiles);

public sealed record BackupWifiProfile(string Name, string RelativePath, string FullPath, string Authentication, bool KeyIncluded, bool? PerUser)
{
    public string KeyLabel => KeyIncluded ? "Clé incluse" : "Profil sans clé";
}

public sealed record BackupWallpaper(string RelativePath, string FullPath, long Size);

/// <summary>Printers archive of a 2.1 backup (.prnbak, PrintBridge format).</summary>
public sealed record BackupPrinters(string RelativePath, string FullPath, long Size, IReadOnlyList<ManifestPrinter> Printers);

/// <summary>Outlook mailbox of a 2.1 backup: the PST (mails, calendar, contacts) and the autocomplete copy.</summary>
public sealed record BackupMailbox(
    string MailboxName,
    Outlook.OutlookCategories Saved,
    string? PstRelative,
    string? PstFullPath,
    string? AutocompleteRelative,
    string? AutocompleteFullPath,
    int Mails,
    int Contacts,
    int Events,
    string Status)
{
    /// <summary>Categories that can really be restored from the files present in the backup.</summary>
    public Outlook.OutlookCategories Available
    {
        get
        {
            var c = Outlook.OutlookCategories.None;
            if (PstFullPath != null) c |= Saved & (Outlook.OutlookCategories.Mails | Outlook.OutlookCategories.Calendar | Outlook.OutlookCategories.Contacts);
            if (AutocompleteFullPath != null) c |= Outlook.OutlookCategories.Autocomplete;
            return c;
        }
    }
}

/// <summary>Normalized description of a backup, whatever its generation. Every FullPath has been checked to be inside <see cref="Root"/>.</summary>
public sealed class BackupContent
{
    public required string Root { get; init; }
    public required BackupGeneration Generation { get; init; }
    public required BackupSourceInfo Source { get; init; }
    public string? BackupId { get; init; }
    public int? SchemaVersion { get; init; }
    public long? TotalFiles { get; init; }
    public long? TotalBytes { get; init; }
    public bool SkipCloudOnly { get; init; } = true;

    public IReadOnlyList<BackupPersonalFolder> Personal { get; init; } = [];
    public IReadOnlyList<BackupAdditionalItem> Additional { get; init; } = [];
    public BackupSignatures? Signatures { get; init; }
    public IReadOnlyList<BackupBrowser> Browsers { get; init; } = [];
    public IReadOnlyList<BackupWifiProfile> Wifi { get; init; } = [];
    public BackupWallpaper? Wallpaper { get; init; }
    public BackupPrinters? Printers { get; init; }
    public BackupMailbox? Mailbox { get; init; }

    /// <summary>Non-blocking remarks (older generation, information unavailable, element missing...).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>Elements refused because their path is unsafe (traversal, absolute path, link...). Any entry blocks the restore.</summary>
    public IReadOnlyList<string> SecurityIssues { get; init; } = [];

    public bool IsLegacy => Generation == BackupGeneration.Legacy;
    public string GenerationLabel => IsLegacy ? "Sauvegarde DataBridge ancienne génération" : "Sauvegarde DataBridge (manifest)";

    public string VersionLabel => Source.DataBridgeVersion is { Length: > 0 } v
        ? $"{AppInfo.Name} {v}"
        : (Source.Product is { Length: > 0 } p ? p : AppInfo.Name) + " 1.x";

    public IEnumerable<string> CategoryLabels()
    {
        if (Personal.Count > 0) yield return "Dossiers personnels";
        if (Additional.Count > 0) yield return "Fichiers supplémentaires";
        if (Signatures != null) yield return "Signatures Outlook";
        if (Browsers.Count > 0) yield return "Navigateurs (" + string.Join(", ", Browsers.Select(b => BrowserProcessService.DisplayName(b.Kind).Split(' ')[^1])) + ")";
        if (Wifi.Count > 0) yield return Format.Plural(Wifi.Count, "profil Wi-Fi", "profils Wi-Fi");
        if (Wallpaper != null) yield return "Fond d'écran";
        if (Printers != null) yield return Format.Plural(Printers.Printers.Count, "imprimante", "imprimantes");
        if (Mailbox != null) yield return "Boîte Outlook";
    }

    public bool IsEmpty => Personal.Count == 0 && Additional.Count == 0 && Signatures is null && Browsers.Count == 0 && Wifi.Count == 0 && Wallpaper is null
                           && Printers is null && Mailbox is null;
}

/// <summary>Result of opening a folder: the content, or why it cannot be restored.</summary>
public sealed record BackupOpenResult(BackupContent? Content, string? Error, IReadOnlyList<string> Candidates)
{
    public bool Success => Content != null;
    public static BackupOpenResult Fail(string error, IReadOnlyList<string>? candidates = null) => new(null, error, candidates ?? []);
}
