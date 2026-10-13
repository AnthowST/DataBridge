using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Restore;

/// <summary>What to do when the destination already holds a file of the same name.</summary>
public enum ConflictPolicy
{
    /// <summary>Replace existing files (identical files — same size and date — are left untouched).</summary>
    Replace,
    /// <summary>Never touch an existing file.</summary>
    Keep,
    /// <summary>Replace only when the backup copy is more recent (default: protects newer work, restores what is missing).</summary>
    ReplaceIfNewer,
}

public static class ConflictPolicies
{
    public static string Label(ConflictPolicy p) => p switch
    {
        ConflictPolicy.Replace => "Remplacer les fichiers existants",
        ConflictPolicy.Keep => "Conserver les fichiers existants",
        _ => "Remplacer uniquement si la sauvegarde est plus récente",
    };
}

/// <summary>Decision for one file of the backup against the destination.</summary>
public enum FileDecision
{
    /// <summary>Absent from the destination: restored.</summary>
    Restore,
    /// <summary>Present and replaced by the backup copy.</summary>
    Replace,
    /// <summary>Present and kept (policy).</summary>
    Keep,
    /// <summary>Present with the same size and date: nothing to do.</summary>
    Identical,
}

public static class ConflictResolver
{
    /// <summary>File-system timestamps differ slightly between NTFS, FAT and network shares.</summary>
    public static readonly TimeSpan DateTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Same rule for the analysis shown in the summary and for the engine, so both always agree. Only names, sizes and
    /// dates are compared: no content is read and no hash is computed.
    /// </summary>
    public static FileDecision Decide(ConflictPolicy policy, long sourceSize, DateTime sourceWriteUtc, FsEntry? existing)
    {
        if (existing is null) return FileDecision.Restore;
        var sameDate = Math.Abs((existing.LastWriteUtc - sourceWriteUtc).TotalSeconds) <= DateTolerance.TotalSeconds;
        // A OneDrive "online only" placeholder with the same size and date is the same file: never downloaded, never replaced.
        if (existing.Size == sourceSize && sameDate) return FileDecision.Identical;
        return policy switch
        {
            ConflictPolicy.Replace => FileDecision.Replace,
            ConflictPolicy.Keep => FileDecision.Keep,
            _ => sourceWriteUtc - existing.LastWriteUtc > DateTolerance ? FileDecision.Replace : FileDecision.Keep,
        };
    }
}

public static class RestoreCategories
{
    public const string Personal = Copy.Categories.Personal;
    public const string Additional = Copy.Categories.Additional;
    public const string Outlook = "Signatures Outlook";
    public const string Browsers = Copy.Categories.Browsers;
    public const string Network = Copy.Categories.Network;
    public const string Windows = Copy.Categories.Windows;
}

/// <summary>
/// One file or folder of the backup and exactly where it goes. The engine and the summary analysis both work from these
/// items; the engine never writes outside <see cref="AllowedRoot"/>.
/// </summary>
public sealed class RestoreItem
{
    public required string Category { get; init; }
    public required string Label { get; init; }
    /// <summary>Absolute path in the backup (checked to be inside the backup).</summary>
    public required string SourcePath { get; init; }
    /// <summary>Path relative to the backup root (log, summary).</summary>
    public required string RelativeSourcePath { get; init; }
    /// <summary>Absolute destination (folder, or target file for a file item).</summary>
    public required string DestinationPath { get; init; }
    public bool IsFile { get; init; }
    /// <summary>Every write must stay at or beneath this path.</summary>
    public string AllowedRoot => DestinationPath;
    /// <summary>What is restored beneath <see cref="SourcePath"/> (null = everything).</summary>
    public SelectionSnapshot? Selection { get; init; }
    public ConflictPolicy Policy { get; init; } = ConflictPolicy.ReplaceIfNewer;
    /// <summary>Paths relative to <see cref="SourcePath"/> that are never restored (non-portable browser data).</summary>
    public IReadOnlyList<string> Excluded { get; init; } = [];
    /// <summary>When set, every existing file about to be replaced is first copied here (browser profiles, wallpaper).</summary>
    public string? SafetyFolder { get; init; }
    /// <summary>Move the stale "-journal" / "-wal" / "-shm" companions of a replaced database to the safety folder.</summary>
    public bool CleanDatabaseCompanions { get; init; }
    /// <summary>Destination detail for the summary and the log ("OneDrive · itelium", "profil actuel Default"...).</summary>
    public string? DestinationNote { get; init; }
    public bool DestinationIsOneDrive { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Browser profile restored by this item (null for files and folders of other categories).</summary>
    public BrowserRestoreTarget? Browser { get; init; }

    public bool IsExcluded(string relative) =>
        Excluded.Any(e => relative.Equals(e, StringComparison.OrdinalIgnoreCase)
                          || relative.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Source profile of the backup and the profile of this PC it is restored into.</summary>
public sealed record BrowserRestoreTarget(
    BrowserKind Kind,
    string SourceProfileFolder,
    string SourceDisplayName,
    string? SourceAccount,
    string TargetProfilePath,
    string TargetLabel,
    bool CreatesProfile,
    /// <summary>Firefox profile created by DataBridge: registered in profiles.ini after the copy.</summary>
    FirefoxRegistration? Firefox);

public sealed record FirefoxRegistration(string FirefoxRoot, string ProfileName, string RelativePath, bool MakeDefault);

public enum WifiExistingPolicy { Keep, Replace }

public sealed record WifiRestoreItem(BackupWifiProfile Profile, bool ExistsOnThisPc);

public sealed record WallpaperRestoreItem(string SourcePath, string RelativeSourcePath, string TranscodedPath, string StoreFolder, bool Apply);

/// <summary>Exactly what the engine will do. Built by <see cref="RestorePlanner"/> from the technician's selection.</summary>
public sealed class RestorePlan
{
    public required BackupContent Backup { get; init; }
    public required RestoreTarget Target { get; init; }
    public required IReadOnlyList<RestoreItem> Items { get; init; }
    public IReadOnlyList<WifiRestoreItem> Wifi { get; init; } = [];
    public WifiExistingPolicy WifiExisting { get; init; } = WifiExistingPolicy.Keep;
    /// <summary>Network drives to reconnect (2.2), with the session rights.</summary>
    public IReadOnlyList<BackupNetworkDrive> NetworkDrives { get; init; } = [];
    /// <summary>When a share asks for a password, Windows asks for it (owned by <see cref="OwnerWindow"/>).</summary>
    public bool AskNetworkPassword { get; init; } = true;
    /// <summary>Main window handle, owner of Windows' credential prompt for the network drives (set by the UI at launch).</summary>
    public IntPtr OwnerWindow { get; set; }
    public WallpaperRestoreItem? Wallpaper { get; init; }
    /// <summary>Printers to restore (PrintBridge plan: only the selected, restorable printers are touched).</summary>
    public DataBridge.Printers.Restore.RestorePlan? Printers { get; init; }
    /// <summary>Outlook mailbox to restore into the chosen mailbox of this profile.</summary>
    public Outlook.OutlookRestoreJob? Outlook { get; init; }
    public ConflictPolicy Policy { get; init; } = ConflictPolicy.ReplaceIfNewer;
    /// <summary>Elements deliberately not restored (log).</summary>
    public IReadOnlyList<string> NotSelected { get; init; } = [];
    /// <summary>Timestamped events about closing the browsers before the restore (written verbatim in the log).</summary>
    public IReadOnlyList<string> BrowserEvents { get; set; } = [];
    /// <summary>Unique folder of this restore under %LOCALAPPDATA%\DataBridge\RestoreSafety.</summary>
    public required string SafetyRoot { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public IEnumerable<BrowserKind> BrowserKinds => Items.Where(i => i.Browser != null).Select(i => i.Browser!.Kind).Distinct();
    public IReadOnlyList<DataBridge.Printers.Restore.PrinterPlan> PrintersToRestore => Printers?.ToRestore.ToList() ?? [];
    /// <summary>One UAC prompt will be shown during the restore (machine operations of the printers only).</summary>
    public bool PrintersNeedAdmin => PrintersToRestore.Count > 0 && Printers!.NeedsAdmin;
    public bool IsEmpty => Items.Count == 0 && Wifi.Count == 0 && NetworkDrives.Count == 0 && Wallpaper is null && PrintersToRestore.Count == 0 && Outlook is null;
}

public enum ValidationSeverity { Blocking, Warning }

public sealed record ValidationIssue(ValidationSeverity Severity, string Message)
{
    public bool IsBlocking => Severity == ValidationSeverity.Blocking;
}
