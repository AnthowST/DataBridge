using DataBridge.Core.IO;
using DataBridge.Core.Profile;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Restore;

/// <summary>A personal folder of the backup, the current Known Folder it goes to, and the selection inside it.</summary>
public sealed class RestorePersonalChoice
{
    public RestorePersonalChoice(BackupPersonalFolder backup, PersonalFolderInfo? target)
    {
        Backup = backup;
        Target = target;
        var def = KnownFolders.Personal.First(d => d.Kind == backup.Kind);
        // The explorer works on the backup folder: same SelectionTree / tri-state model as the backup workflow.
        Root = new PersonalRoot(new PersonalFolderInfo(def, backup.FullPath, Directory.Exists(backup.FullPath), FolderLocation.Local, null));
    }

    public BackupPersonalFolder Backup { get; }
    /// <summary>Current Known Folder of this session (null when Windows does not know it).</summary>
    public PersonalFolderInfo? Target { get; }
    public PersonalRoot Root { get; }
    public string Label => Backup.Label;
    public string? DestinationPath => Target?.Path;
    public bool DestinationIsOneDrive => Target?.Location == FolderLocation.OneDrive;
}

public enum AdditionalDestinationKind
{
    /// <summary>The original path, still valid on this PC.</summary>
    Original,
    /// <summary>Original path inside the old profile, adapted to the current profile.</summary>
    CurrentProfile,
    /// <summary>Chosen by the technician.</summary>
    Custom,
    /// <summary>Unknown or unusable on this PC: the technician must choose.</summary>
    Undefined,
}

/// <summary>A file or folder added manually at backup time, with where it goes back.</summary>
public sealed class RestoreAdditionalChoice
{
    public RestoreAdditionalChoice(BackupAdditionalItem item, string? proposedPath, AdditionalDestinationKind kind, string note)
    {
        Item = item;
        ProposedPath = proposedPath;
        ProposedKind = kind;
        ProposalNote = note;
    }

    public BackupAdditionalItem Item { get; }
    public bool Selected { get; set; } = true;
    public string? ProposedPath { get; }
    public AdditionalDestinationKind ProposedKind { get; }
    public string ProposalNote { get; }
    /// <summary>Folder chosen by the technician: the item is restored as &lt;folder&gt;\&lt;name&gt;.</summary>
    public string? CustomParent { get; set; }

    public AdditionalDestinationKind Kind => CustomParent != null ? AdditionalDestinationKind.Custom : ProposedKind;
    public string? DestinationPath => CustomParent != null ? Path.Combine(CustomParent, Item.Name) : ProposedPath;
    public bool HasDestination => DestinationPath != null;
}

/// <summary>A saved browser profile and the profile of this PC it is restored into.</summary>
public sealed class RestoreBrowserChoice
{
    public RestoreBrowserChoice(BackupBrowser browser, BackupBrowserProfile profile, IReadOnlyList<BrowserTargetOption> options, string? newFirefoxFolder)
    {
        Browser = browser;
        Profile = profile;
        Options = options;
        NewFirefoxFolder = newFirefoxFolder;
        Target = BrowserRestoreService.DefaultOption(browser.Kind, options);
    }

    public BackupBrowser Browser { get; }
    public BackupBrowserProfile Profile { get; }
    public IReadOnlyList<BrowserTargetOption> Options { get; }
    public string? NewFirefoxFolder { get; }
    public bool Selected { get; set; } = true;
    public BrowserTargetOption? Target { get; set; }
}

/// <summary>The Outlook mailbox of the backup (2.1) and the mailbox of this Outlook profile it is restored into.</summary>
public sealed class RestoreMailboxChoice(BackupMailbox source)
{
    public BackupMailbox Source { get; } = source;
    /// <summary>Checked categories (everything of each category is restored).</summary>
    public Outlook.OutlookCategories Selected { get; set; } = source.Available;
    /// <summary>Target mailbox, always chosen explicitly (never guessed).</summary>
    public string? TargetStoreId { get; set; }
    public string? TargetName { get; set; }
    /// <summary>False (default): merge into the matching folders (Inbox, Sent, Calendar...). True: a "Restauration &lt;date&gt;" folder.</summary>
    public bool IntoNewFolder { get; set; }
    /// <summary>Also replace the autocomplete cache of the target (its current cache is copied first). Not guaranteed by Outlook.</summary>
    public bool ReplaceAutocompleteCache { get; set; }
    public bool Include => (Selected & Source.Available) != Outlook.OutlookCategories.None;
}

/// <summary>
/// Everything the technician chose for the restore, independent from the UI. <see cref="RestorePlanner.BuildPlan"/> turns
/// it into the exact plan executed by the engine and shown in the summary.
/// </summary>
public sealed class RestoreSelection
{
    public required BackupContent Backup { get; init; }
    public required RestoreTarget Target { get; init; }
    public List<RestorePersonalChoice> Personal { get; } = [];
    public List<RestoreAdditionalChoice> Additional { get; } = [];
    public bool IncludeSignatures { get; set; }
    public List<RestoreBrowserChoice> Browsers { get; } = [];
    /// <summary>Selected Wi-Fi profiles (by relative file path in the backup).</summary>
    public HashSet<string> SelectedWifi { get; } = new(StringComparer.OrdinalIgnoreCase);
    public WifiExistingPolicy WifiExisting { get; set; } = WifiExistingPolicy.Keep;
    public WifiEnvironment? WifiState { get; set; }
    public bool IncludeWallpaper { get; set; }
    public ConflictPolicy Policy { get; set; } = ConflictPolicy.ReplaceIfNewer;
    /// <summary>Outlook mailbox of the backup (null when the backup has none).</summary>
    public RestoreMailboxChoice? Mailbox { get; set; }
    /// <summary>Printers archive of the backup, checked and analysed against this PC (null until the analysis is done).</summary>
    public PrinterTransfer.PrinterRestoreSource? Printers { get; set; }

    /// <summary>Default choices: everything present in the backup is selected; destinations come from this session.</summary>
    public static RestoreSelection Create(BackupContent backup, RestoreTarget target)
    {
        var s = new RestoreSelection { Backup = backup, Target = target };
        foreach (var p in backup.Personal)
            s.Personal.Add(new RestorePersonalChoice(p, target.Folder(p.Kind)));
        foreach (var a in backup.Additional)
        {
            var (path, kind, note) = ProposeDestination(a, backup, target);
            s.Additional.Add(new RestoreAdditionalChoice(a, path, kind, note));
        }
        s.IncludeSignatures = backup.Signatures != null;
        foreach (var b in backup.Browsers)
        {
            foreach (var p in b.Profiles)
            {
                var folder = b.Kind == Detection.BrowserKind.Firefox ? BrowserRestoreService.NewFirefoxFolderName(p.DisplayName) : null;
                s.Browsers.Add(new RestoreBrowserChoice(b, p, BrowserRestoreService.TargetOptions(b.Kind, target, p, folder), folder));
            }
        }
        foreach (var w in backup.Wifi) s.SelectedWifi.Add(w.RelativePath);
        s.IncludeWallpaper = backup.Wallpaper != null;
        if (backup.Mailbox != null) s.Mailbox = new RestoreMailboxChoice(backup.Mailbox);
        return s;
    }

    /// <summary>
    /// Where an additional element goes back: its original path when it is still valid here, adapted to the current
    /// profile when it was inside the old one, otherwise undefined (never guessed).
    /// </summary>
    public static (string? Path, AdditionalDestinationKind Kind, string Note) ProposeDestination(BackupAdditionalItem item, BackupContent backup, RestoreTarget target)
    {
        var original = item.OriginalPath;
        if (original is null)
            return (null, AdditionalDestinationKind.Undefined, "Chemin d'origine inconnu : destination à choisir.");

        var candidate = original;
        var kind = AdditionalDestinationKind.Original;
        var sourceProfile = backup.Source.ProfilePath;
        if (sourceProfile != null && PathUtil.IsSameOrUnder(original, sourceProfile) && !PathUtil.AreSame(sourceProfile, target.Profile.ProfilePath))
        {
            var relative = PathUtil.Relative(original, sourceProfile);
            candidate = relative.Length == 0 ? target.Profile.ProfilePath : Path.Combine(target.Profile.ProfilePath, relative);
            kind = AdditionalDestinationKind.CurrentProfile;
        }

        if (RestorePaths.IsProtectedLocation(candidate))
            return (null, AdditionalDestinationKind.Undefined, "Destination originale dans un dossier système : destination à choisir.");
        if (target.Profile.IsInOtherUserProfile(candidate))
            return (null, AdditionalDestinationKind.Undefined, "Destination originale dans le profil d'un autre utilisateur : destination à choisir.");
        if (RestorePaths.IsInside(candidate, backup.Root) || RestorePaths.IsInside(backup.Root, candidate))
            return (null, AdditionalDestinationKind.Undefined, "Destination originale confondue avec la sauvegarde : destination à choisir.");

        var root = Path.GetPathRoot(candidate);
        bool rootExists;
        try { rootExists = root != null && Directory.Exists(root); }
        catch { rootExists = false; }
        if (!rootExists)
            return (null, AdditionalDestinationKind.Undefined,
                $"Destination originale indisponible ({(root ?? candidate).TrimEnd('\\')} absent sur ce PC) : destination à choisir.");

        return kind == AdditionalDestinationKind.CurrentProfile
            ? (candidate, kind, $"Chemin d'origine dans le profil {backup.Source.User ?? "d'origine"} ({original}) : adapté au profil actuel.")
            : (candidate, kind, "Destination originale.");
    }
}
