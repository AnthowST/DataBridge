using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Profile;

namespace DataBridge.Core.Selection;

/// <summary>A personal Known Folder with its selection tree (null tree when the folder does not exist).</summary>
public sealed class PersonalRoot(PersonalFolderInfo info)
{
    public PersonalFolderInfo Info { get; } = info;
    public SelectionTree? Tree { get; } = info.Exists && info.Path != null ? new SelectionTree(info.Path) : null;
    public bool Available => Tree != null;
    public string Label => Info.Definition.DisplayName;
    public string Path => Tree?.RootPath ?? Info.Path ?? "";
    public CheckState State => Tree?.Snapshot().RootState ?? CheckState.Unchecked;
}

/// <summary>File or folder added manually by the technician.</summary>
public sealed class AdditionalItem(string path, bool isDirectory)
{
    public string Path { get; } = PathUtil.Normalize(path);
    public bool IsDirectory { get; } = isDirectory;
    public string Name => PathUtil.DisplayName(Path);
}

public enum AdditionalStatusKind { Ready, AlreadyIncluded, PartiallyIncluded, ContainsPersonal, Missing, ContainsDestination }

public sealed record AdditionalStatus(AdditionalStatusKind Kind, string Message, string? DestinationRelative)
{
    /// <summary>
    /// Part of the plan. An item overlapping the backup folder stays in the plan on purpose: the launch is then
    /// blocked by <see cref="DestinationGuard"/> instead of silently dropping the item.
    /// </summary>
    public bool WillCopy => Kind is AdditionalStatusKind.Ready or AdditionalStatusKind.PartiallyIncluded
        or AdditionalStatusKind.ContainsPersonal or AdditionalStatusKind.ContainsDestination;
}

/// <summary>Browser data selected for backup (profile folders by full path).</summary>
public sealed class BrowserSelection(BrowserInfo info)
{
    public BrowserInfo Info { get; } = info;
    public HashSet<string> SelectedProfiles { get; } = new(
        info.Profiles.Where(p => p.PreSelected).Select(p => p.FullPath), PathUtil.Comparer);
    public bool Include => Info.Detected && SelectedProfiles.Count > 0;
}

/// <summary>
/// Everything the technician selected, independent from the UI. <see cref="BuildPlan"/> turns it into copy jobs,
/// resolving duplicates between manual additions and personal folders.
/// </summary>
public sealed class BackupSelection
{
    public required ProfileInfo Profile { get; init; }
    public List<PersonalRoot> PersonalRoots { get; } = [];
    public List<AdditionalItem> Additional { get; } = [];
    public SignatureInfo? Signatures { get; set; }
    public bool IncludeSignatures { get; set; } = true;
    public List<BrowserSelection> Browsers { get; } = [];
    public WallpaperInfo? Wallpaper { get; set; }
    public bool IncludeWallpaper { get; set; } = true;
    public List<WifiProfileInfo> WifiProfiles { get; } = [];
    public HashSet<string> SelectedWifi { get; } = new(StringComparer.Ordinal);
    /// <summary>Outlook mailbox to export (2.1): chosen on the Outlook page, null when none was chosen.</summary>
    public Outlook.OutlookBackupJob? OutlookMailbox { get; set; }
    /// <summary>Printers of this PC (2.1), read on the Imprimantes page; null until then.</summary>
    public DataBridge.Printers.Inventory.LiveInventory? PrinterInventory { get; set; }
    /// <summary>Checked printers (PrintBridge printer ids).</summary>
    public HashSet<string> SelectedPrinters { get; } = new(StringComparer.Ordinal);
    /// <summary>OneDrive accounts of the session (identification only), reported in the log.</summary>
    public OneDriveDetection OneDrive { get; set; } = OneDriveDetection.Empty;
    /// <summary>Do not download "online-only" OneDrive/cloud files (default).</summary>
    public bool SkipCloudOnly { get; set; } = true;

    /// <summary>Personal folders located inside another personal folder (e.g. Pictures redirected into Documents).</summary>
    public IEnumerable<PersonalRoot> NestedIn(PersonalRoot root) =>
        PersonalRoots.Where(o => o != root && o.Available && root.Available && PathUtil.IsStrictlyUnder(o.Path, root.Path));

    /// <summary>The personal folder (deepest) containing a path, if any.</summary>
    public PersonalRoot? OwnerOf(string path) =>
        PersonalRoots.Where(r => r.Available && PathUtil.IsSameOrUnder(path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();

    /// <param name="backupRoot">Backup folder that will be created (when the destination is known): overlaps are flagged.</param>
    public IReadOnlyDictionary<AdditionalItem, AdditionalStatus> AnalyzeAdditional(string? backupRoot = null)
    {
        var result = new Dictionary<AdditionalItem, AdditionalStatus>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Additional)
        {
            var exists = item.IsDirectory ? Directory.Exists(item.Path) : File.Exists(item.Path);
            if (!exists)
            {
                result[item] = new AdditionalStatus(AdditionalStatusKind.Missing, "Élément introuvable ou inaccessible avec les droits actuels.", null);
                continue;
            }

            var kindWord = item.IsDirectory ? "dossier" : "fichier";
            if (backupRoot != null && DestinationGuard.Overlap(item.Path, backupRoot) is { } overlap)
            {
                result[item] = new AdditionalStatus(AdditionalStatusKind.ContainsDestination,
                    overlap == OverlapKind.BackupInsideSource
                        ? $"Ce {kindWord} contient la destination de la sauvegarde : il serait recopié dans lui-même. Retirez-le ou choisissez une autre destination."
                        : $"Ce {kindWord} se trouve dans le dossier de sauvegarde : il ne peut pas être copié. Retirez-le ou choisissez une autre destination.",
                    UniqueAdditionalName(item, usedNames));
                continue;
            }

            var container = Additional
                .Where(o => o != item && o.IsDirectory && PathUtil.IsStrictlyUnder(item.Path, o.Path))
                .OrderBy(o => o.Path.Length)
                .FirstOrDefault();
            if (container != null)
            {
                result[item] = new AdditionalStatus(AdditionalStatusKind.AlreadyIncluded,
                    $"Ce {kindWord} est déjà inclus via {container.Path}.", null);
                continue;
            }

            var owner = OwnerOf(item.Path);
            string Destination() => UniqueAdditionalName(item, usedNames);

            if (owner != null)
            {
                var snapshot = owner.Tree!.Snapshot();
                var state = item.IsDirectory ? snapshot.StateOf(item.Path) : snapshot.IsIncluded(item.Path) ? CheckState.Checked : CheckState.Unchecked;
                if (state == CheckState.Checked)
                {
                    result[item] = new AdditionalStatus(AdditionalStatusKind.AlreadyIncluded,
                        $"Ce {kindWord} est déjà inclus via {owner.Label}.", null);
                    continue;
                }
                var dest = Destination();
                result[item] = state == CheckState.Indeterminate
                    ? new AdditionalStatus(AdditionalStatusKind.PartiallyIncluded,
                        $"Partiellement inclus via {owner.Label} : seuls les éléments non cochés dans {owner.Label} seront copiés dans {dest}.", dest)
                    : new AdditionalStatus(AdditionalStatusKind.Ready,
                        $"Non coché dans {owner.Label} : sera copié dans {dest}.", dest);
                continue;
            }

            if (item.IsDirectory)
            {
                var inside = PersonalRoots
                    .Where(r => r.Available && PathUtil.IsStrictlyUnder(r.Path, item.Path) && r.State != CheckState.Unchecked)
                    .Select(r => r.Label).ToList();
                if (inside.Count > 0)
                {
                    var dest = Destination();
                    result[item] = new AdditionalStatus(AdditionalStatusKind.ContainsPersonal,
                        $"Contient {string.Join(", ", inside)} : déjà géré(s) dans Dossiers personnels, sans doublon. Le reste sera copié dans {dest}.", dest);
                    continue;
                }
            }

            var d = Destination();
            result[item] = new AdditionalStatus(AdditionalStatusKind.Ready, $"Sera copié dans {d}.", d);
        }
        return result;
    }

    private static string UniqueAdditionalName(AdditionalItem item, HashSet<string> usedNames)
    {
        var baseName = PathUtil.SanitizeSegment(item.Name);
        var name = baseName;
        var n = 2;
        while (!usedNames.Add(name))
            name = item.IsDirectory || !Path.HasExtension(baseName)
                ? $"{baseName} ({n++})"
                : $"{Path.GetFileNameWithoutExtension(baseName)} ({n++}){Path.GetExtension(baseName)}";
        return @"Additional\" + name;
    }

    public CopyPlan BuildPlan(string destinationBase, string backupFolderName)
    {
        var backupRoot = Path.Combine(destinationBase, backupFolderName);
        var backupCoverage = new SubtreeCoverage(PathUtil.Normalize(backupRoot), "dossier de sauvegarde");
        var jobs = new List<CopyJob>();
        var notSelected = new List<string>();
        var seed = new Restore.ManifestSeed();

        // 1. Personal folders
        foreach (var root in PersonalRoots)
        {
            if (!root.Available)
            {
                notSelected.Add($"{root.Label} : introuvable ({root.Info.Path ?? "chemin inconnu"})");
                continue;
            }
            var snapshot = root.Tree!.Snapshot();
            if (snapshot.RootState == CheckState.Unchecked)
            {
                notSelected.Add($"{root.Label} : non sélectionné");
                continue;
            }
            var nested = NestedIn(root).Select(n => (ICoverage)new SubtreeCoverage(n.Path, $"géré dans {n.Label}")).ToList();
            var personalJob = new CopyJob
            {
                Category = Categories.Personal,
                Label = root.Label,
                SourcePath = root.Path,
                DestinationRelative = @"Personal\" + root.Info.Definition.DestinationName,
                Selection = snapshot,
                Coverage = CompositeCoverage.Combine(nested.Append(backupCoverage)),
                HandledElsewhere = CompositeCoverage.Combine(nested),
            };
            jobs.Add(personalJob);
            seed.Personal.Add((root.Info, personalJob, snapshot.RootState == CheckState.Indeterminate));
        }

        // 2. Manual additions
        foreach (var (item, status) in AnalyzeAdditional(backupRoot))
        {
            if (!status.WillCopy)
            {
                notSelected.Add($"{item.Path} : {status.Message}");
                continue;
            }
            var coverages = new List<ICoverage>();
            var owner = OwnerOf(item.Path);
            if (owner != null && owner.State != CheckState.Unchecked)
                coverages.Add(new SelectionCoverage(owner.Tree!.Snapshot(), owner.Label));
            if (item.IsDirectory)
                coverages.AddRange(PersonalRoots
                    .Where(r => r.Available && PathUtil.IsStrictlyUnder(r.Path, item.Path) && r.State != CheckState.Unchecked)
                    .Select(r => new SubtreeCoverage(r.Path, $"géré dans {r.Label}")));

            var additionalJob = new CopyJob
            {
                Category = Categories.Additional,
                Label = item.Name,
                SourcePath = item.Path,
                DestinationRelative = status.DestinationRelative!,
                IsFile = !item.IsDirectory,
                Coverage = CompositeCoverage.Combine(coverages.Append(backupCoverage)),
                HandledElsewhere = CompositeCoverage.Combine(coverages),
            };
            jobs.Add(additionalJob);
            seed.Additional.Add((item, additionalJob));
        }

        // 3. Outlook signatures
        if (Signatures is { Exists: true } sig && IncludeSignatures)
        {
            var signatureJob = new CopyJob
            {
                Category = Categories.Outlook,
                Label = "Signatures Outlook",
                SourcePath = sig.Path,
                DestinationRelative = @"Outlook\Signatures",
                Coverage = backupCoverage,
            };
            jobs.Add(signatureJob);
            seed.Signatures = (sig, signatureJob);
        }

        // 4. Browsers (profile folders minus caches; passwords are never extracted)
        foreach (var browser in Browsers.Where(b => b.Include))
        {
            var browserJobs = BuildBrowserJobs(browser, backupCoverage).ToList();
            jobs.AddRange(browserJobs);
            seed.Browsers.Add((browser.Info, browser.Info.Profiles.Where(p => browser.SelectedProfiles.Contains(p.FullPath)).ToList(), browserJobs));
        }

        // 5. Wallpaper
        if (Wallpaper is { Exists: true } wp && IncludeWallpaper)
        {
            var wallpaperJob = new CopyJob
            {
                Category = Categories.Windows,
                Label = "Fond d'écran",
                SourcePath = wp.Path,
                DestinationRelative = @"Windows\Wallpaper\TranscodedWallpaper",
                IsFile = true,
            };
            jobs.Add(wallpaperJob);
            seed.Wallpaper = (wp, wallpaperJob);
        }

        // 6. Wi-Fi
        var wifi = WifiProfiles.Where(p => SelectedWifi.Contains(p.Name)).ToList();

        // 7. Printers (export to a .prnbak, read-only) and Outlook mailbox (new PST), after the files.
        var printerIds = PrinterInventory?.Printers.Where(p => SelectedPrinters.Contains(p.Entry.Id)).Select(p => p.Entry.Id).ToList() ?? [];
        if (PrinterInventory != null)
            foreach (var p in PrinterInventory.Printers.Where(p => !SelectedPrinters.Contains(p.Entry.Id)))
                notSelected.Add($"Imprimante {p.Entry.Name} : non sélectionnée");
        var mailbox = OutlookMailbox is { Categories: not Outlook.OutlookCategories.None } m ? m : null;

        return new CopyPlan
        {
            Profile = Profile,
            DestinationBase = destinationBase,
            BackupFolderName = backupFolderName,
            Jobs = jobs,
            Wifi = wifi.Count > 0 ? new WifiJob(wifi, @"Network\WiFi") : null,
            Printers = printerIds.Count > 0 ? new PrinterTransfer.PrinterBackupJob(PrinterInventory!, printerIds) : null,
            Outlook = mailbox,
            SkipCloudOnly = SkipCloudOnly,
            NotSelected = notSelected,
            OneDriveLines = OneDriveLogLines(),
            ManifestSeed = seed,
        };
    }

    /// <summary>Accounts (name, type, root) and the Known Folder mapping. No e-mail, no token.</summary>
    private List<string> OneDriveLogLines()
    {
        var lines = new List<string>();
        if (OneDrive.Accounts.Count == 0 && OneDrive.Roots.Count == 0 && OneDrive.IgnoredKeys.Count == 0
            && PersonalRoots.All(r => r.Info.Location != FolderLocation.OneDrive))
            return lines;

        foreach (var a in OneDrive.Accounts)
            lines.Add($"Compte OneDrive détecté : {a.LogName} ({a.TypeLabel}, clé {a.AccountId}) — racine {a.UserFolder ?? string.Join(", ", a.SyncRoots)}");
        foreach (var r in OneDrive.Roots.Where(r => r.Account is null))
            lines.Add($"Racine OneDrive sans compte identifié : {r.Path}");
        foreach (var k in OneDrive.IgnoredKeys)
            lines.Add($"Clé de compte présente sans dossier local (compte non configuré sur ce PC) : {k}");
        if (OneDrive.Accounts.Count == 0 && OneDrive.Roots.Count == 0)
            lines.Add("Aucun compte OneDrive configuré pour cette session.");

        lines.Add("Dossiers personnels :");
        foreach (var root in PersonalRoots)
        {
            var target = root.Info.Location switch
            {
                FolderLocation.OneDrive when root.Info.OneDriveAccount is { } acc => $"OneDrive « {acc.LogName} »",
                FolderLocation.OneDrive => $"OneDrive (compte non identifié)",
                FolderLocation.Network => "réseau (redirigé)",
                _ => "local",
            };
            lines.Add($"  {root.Label} -> {target}" + (root.Info.Path is { } p ? $" ({p})" : ""));
        }
        return lines;
    }

    private static IEnumerable<CopyJob> BuildBrowserJobs(BrowserSelection browser, ICoverage backupCoverage)
    {
        var info = browser.Info;
        var folderName = info.Kind switch { BrowserKind.Chrome => "Chrome", BrowserKind.Edge => "Edge", _ => "Firefox" };
        // Chrome / Edge: the complete "User Data" folder (profiles and the browser-wide data next to them), minus the caches
        // and the profiles the technician unchecked. Firefox: profiles.ini / installs.ini and the selected profiles.
        var wholeUserData = info.Kind != BrowserKind.Firefox;
        var tree = new SelectionTree(info.DataRoot, rootIncluded: wholeUserData);
        if (wholeUserData)
        {
            foreach (var cache in info.ExcludedSubfolders)
                tree.AddRule(Path.Combine(info.DataRoot, cache), false);
            foreach (var profile in info.Profiles.Where(p => !browser.SelectedProfiles.Contains(p.FullPath) && PathUtil.IsStrictlyUnder(p.FullPath, info.DataRoot)))
                tree.AddRule(profile.FullPath, false);
        }
        else
        {
            foreach (var file in info.RootFiles)
                tree.AddRule(Path.Combine(info.DataRoot, file), true);
        }

        var external = new List<BrowserProfileInfo>();
        var copiedProfiles = new List<string>();
        foreach (var profile in info.Profiles.Where(p => browser.SelectedProfiles.Contains(p.FullPath)))
        {
            if (!PathUtil.IsStrictlyUnder(profile.FullPath, info.DataRoot))
            {
                external.Add(profile);
                continue;
            }
            tree.AddRule(profile.FullPath, true);
            foreach (var cache in info.ExcludedSubfolders)
                tree.AddRule(Path.Combine(profile.FullPath, cache), false);
            copiedProfiles.Add(profile.FolderName);
        }

        var notes = new List<string>
        {
            "Profils : " + string.Join(", ", copiedProfiles),
            "Caches exclus : " + string.Join(", ", info.ExcludedSubfolders),
            "Mots de passe : non extraits (protégés par Windows / le navigateur, à exporter manuellement).",
        };
        if (wholeUserData) notes.Insert(0, "Dossier User Data complet (profils et données communes du navigateur).");

        if (copiedProfiles.Count > 0)
        {
            yield return new CopyJob
            {
                Category = Categories.Browsers,
                Label = info.Name,
                SourcePath = info.DataRoot,
                DestinationRelative = info.Kind == BrowserKind.Firefox ? @"Browsers\Firefox" : $@"Browsers\{folderName}\User Data",
                Selection = tree.Snapshot(),
                Coverage = backupCoverage,
                Notes = notes,
            };
        }

        foreach (var profile in external)
        {
            var t = new SelectionTree(profile.FullPath);
            foreach (var cache in info.ExcludedSubfolders)
                t.AddRule(Path.Combine(profile.FullPath, cache), false);
            yield return new CopyJob
            {
                Category = Categories.Browsers,
                Label = $"{info.Name} ({profile.DisplayName})",
                SourcePath = profile.FullPath,
                DestinationRelative = $@"Browsers\{folderName}\Profils-externes\{PathUtil.SanitizeSegment(profile.FolderName)}",
                Selection = t.Snapshot(),
                Coverage = backupCoverage,
                Notes = notes,
            };
        }
    }
}
