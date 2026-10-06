using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Profile;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Restore;

/// <summary>What the analysis found for one item (same rules as the engine).</summary>
public sealed class RestoreItemAnalysis
{
    public long Files;
    public long Bytes;
    public long NewFiles;
    public long NewBytes;
    public long ReplaceFiles;
    public long ReplaceBytes;
    /// <summary>Size of the existing files that will be replaced (space and safety copies).</summary>
    public long ReplacedExistingBytes;
    public long KeepFiles;
    public long IdenticalFiles;
    public long Skipped;
    public long Errors;

    public long WriteFiles => NewFiles + ReplaceFiles;
    public long WriteBytes => NewBytes + ReplaceBytes;
    public long ExistingFiles => ReplaceFiles + KeepFiles + IdenticalFiles;
}

/// <summary>Free space needed on one volume (destinations can be on several volumes).</summary>
public sealed record VolumeNeed(string Root, long NeededBytes, long? FreeBytes)
{
    public const long SafetyMargin = 64L * 1024 * 1024;
    public bool Sufficient => FreeBytes is not { } f || NeededBytes + SafetyMargin <= f;
}

/// <summary>Pre-scan of a plan: files to restore, files already present, replaced / kept, space per volume.</summary>
public sealed class RestoreAnalysis
{
    public Dictionary<RestoreItem, RestoreItemAnalysis> PerItem { get; } = [];
    public List<VolumeNeed> Volumes { get; } = [];
    public int WifiCount { get; init; }
    public bool HasWallpaper { get; init; }

    private long Sum(Func<RestoreItemAnalysis, long> f) => PerItem.Values.Sum(f);
    public long Files => Sum(a => a.Files);
    public long Bytes => Sum(a => a.Bytes);
    public long WriteFiles => Sum(a => a.WriteFiles);
    public long WriteBytes => Sum(a => a.WriteBytes);
    public long NewFiles => Sum(a => a.NewFiles);
    public long ReplaceFiles => Sum(a => a.ReplaceFiles);
    public long KeepFiles => Sum(a => a.KeepFiles);
    public long IdenticalFiles => Sum(a => a.IdenticalFiles);
    public long ExistingFiles => Sum(a => a.ExistingFiles);
    public long Skipped => Sum(a => a.Skipped);
    public long Errors => Sum(a => a.Errors);
    public bool SpaceOk => Volumes.All(v => v.Sufficient);
}

/// <summary>
/// Turns a <see cref="RestoreSelection"/> into the <see cref="RestorePlan"/> executed by the engine, checks it before
/// the launch (dry run), and analyses it against the destination (conflicts, space). The UI never builds items itself.
/// </summary>
public static class RestorePlanner
{
    public static RestorePlan BuildPlan(RestoreSelection s)
    {
        var backup = s.Backup;
        var target = s.Target;
        var safetyRoot = Path.Combine(target.SafetyBase, $"restore-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}");
        var items = new List<RestoreItem>();
        var notSelected = new List<string>();

        // 1. Personal folders -> current Known Folders (never the old absolute paths).
        foreach (var p in s.Personal)
        {
            var snapshot = p.Root.Tree?.Snapshot();
            if (snapshot is null) { notSelected.Add($"{p.Label} : absent de la sauvegarde"); continue; }
            if (snapshot.RootState == CheckState.Unchecked) { notSelected.Add($"{p.Label} : non sélectionné"); continue; }
            if (p.DestinationPath is null) { notSelected.Add($"{p.Label} : dossier introuvable sur ce PC"); continue; }
            items.Add(new RestoreItem
            {
                Category = RestoreCategories.Personal,
                Label = p.Label,
                SourcePath = p.Backup.FullPath,
                RelativeSourcePath = p.Backup.RelativePath,
                DestinationPath = p.DestinationPath,
                Selection = snapshot,
                Policy = s.Policy,
                DestinationIsOneDrive = p.DestinationIsOneDrive,
                DestinationNote = p.Target is { Location: FolderLocation.OneDrive } t
                    ? (t.OneDrive?.Title ?? "OneDrive") + (t.OneDrive?.Account?.Email is { Length: > 0 } mail ? " · " + mail : "")
                    : p.Target?.Location == FolderLocation.Network ? "Réseau (redirigé)" : "Local",
            });
        }

        // 2. Additional elements -> original / adapted / chosen destination.
        foreach (var a in s.Additional)
        {
            if (!a.Selected) { notSelected.Add($"{a.Item.RelativePath} : non sélectionné"); continue; }
            if (a.DestinationPath is null) continue; // blocking issue reported by Validate
            items.Add(new RestoreItem
            {
                Category = RestoreCategories.Additional,
                Label = a.Item.Name,
                SourcePath = a.Item.FullPath,
                RelativeSourcePath = a.Item.RelativePath,
                DestinationPath = PathUtil.Normalize(a.DestinationPath),
                IsFile = a.Item.IsFile,
                Policy = s.Policy,
                DestinationNote = a.Kind switch
                {
                    AdditionalDestinationKind.Original => "destination originale",
                    AdditionalDestinationKind.CurrentProfile => "chemin d'origine adapté au profil actuel",
                    _ => "destination choisie par le technicien",
                },
                Notes = a.Item.OriginalPath is { } o ? [$"Chemin d'origine : {o}"] : ["Chemin d'origine inconnu"],
            });
        }

        // 3. Outlook signatures -> %APPDATA%\Microsoft\Signatures of this session.
        if (backup.Signatures is { } sig)
        {
            if (s.IncludeSignatures)
                items.Add(new RestoreItem
                {
                    Category = RestoreCategories.Outlook,
                    Label = "Signatures Outlook",
                    SourcePath = sig.FullPath,
                    RelativeSourcePath = sig.RelativePath,
                    DestinationPath = target.SignaturesPath,
                    Policy = s.Policy,
                    DestinationNote = "Outlook classique",
                    Notes = ["Signatures d'Outlook classique uniquement : les signatures cloud du nouvel Outlook ne sont pas concernées."],
                });
            else notSelected.Add("Signatures Outlook : non sélectionnées");
        }

        // 4. Browsers -> chosen profile of this PC, through the browser strategy (never the old Local State).
        foreach (var b in s.Browsers)
        {
            if (!b.Selected) { notSelected.Add($"{b.Browser.Name} — {b.Profile.DisplayName} : non sélectionné"); continue; }
            if (b.Target is not { } option) continue; // blocking issue reported by Validate
            var kind = b.Browser.Kind;
            FirefoxRegistration? registration = null;
            if (kind == BrowserKind.Firefox && option.CreatesProfile)
            {
                var root = BrowserRestoreService.FirefoxRoot(target);
                var hasProfiles = target.Browser(BrowserKind.Firefox) is { Detected: true } ff && ff.Profiles.Count > 0;
                registration = new FirefoxRegistration(root, $"{b.Profile.DisplayName} (DataBridge)",
                    PathUtil.Relative(PathUtil.Normalize(option.ProfilePath), PathUtil.Normalize(root)), MakeDefault: !hasProfiles);
            }
            var targetFolder = PathUtil.DisplayName(option.ProfilePath);
            items.Add(new RestoreItem
            {
                Category = RestoreCategories.Browsers,
                Label = $"{b.Browser.Name} — {b.Profile.DisplayName}",
                SourcePath = b.Profile.FullPath,
                RelativeSourcePath = b.Profile.RelativePath,
                DestinationPath = PathUtil.Normalize(option.ProfilePath),
                Policy = ConflictPolicy.Replace,
                Excluded = BrowserRestoreService.NotRestored(kind),
                SafetyFolder = Path.Combine(safetyRoot, "Browsers", kind.ToString(), PathUtil.SanitizeSegment(targetFolder)),
                CleanDatabaseCompanions = true,
                DestinationNote = option.Label + (option.Detail.Length > 0 ? $" ({option.Detail})" : ""),
                Notes = BrowserRestoreService.Description(kind),
                Browser = new BrowserRestoreTarget(kind, b.Profile.Folder, b.Profile.DisplayName, b.Profile.Account,
                    PathUtil.Normalize(option.ProfilePath), option.Label, option.CreatesProfile, registration),
            });
        }

        // 5. Wi-Fi
        var wifi = backup.Wifi.Where(w => s.SelectedWifi.Contains(w.RelativePath))
            .Select(w => new WifiRestoreItem(w, s.WifiState?.ExistingProfiles.Contains(w.Name) == true)).ToList();
        foreach (var w in backup.Wifi.Where(w => !s.SelectedWifi.Contains(w.RelativePath)))
            notSelected.Add($"Wi-Fi {w.Name} : non sélectionné");

        // 6. Wallpaper
        WallpaperRestoreItem? wallpaper = null;
        if (backup.Wallpaper is { } wp)
        {
            if (s.IncludeWallpaper)
                wallpaper = new WallpaperRestoreItem(wp.FullPath, wp.RelativePath, target.TranscodedWallpaperPath, target.WallpaperStore, !target.SkipWallpaperApply);
            else notSelected.Add("Fond d'écran : non sélectionné");
        }

        return new RestorePlan
        {
            Backup = backup,
            Target = target,
            Items = items,
            Wifi = wifi,
            WifiExisting = s.WifiExisting,
            Wallpaper = wallpaper,
            Policy = s.Policy,
            NotSelected = notSelected,
            SafetyRoot = safetyRoot,
        };
    }

    /// <summary>
    /// Dry run before the launch: everything that must be true for the restore to start, plus non-blocking warnings.
    /// With an <paramref name="analysis"/>, free space per volume is checked too.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(RestoreSelection s, RestorePlan plan, RestoreAnalysis? analysis = null)
    {
        var issues = new List<ValidationIssue>();
        void Block(string m) => issues.Add(new ValidationIssue(ValidationSeverity.Blocking, m));
        void Warn(string m) => issues.Add(new ValidationIssue(ValidationSeverity.Warning, m));

        foreach (var security in s.Backup.SecurityIssues) Block("Sauvegarde refusée par sécurité — " + security);
        if (!Directory.Exists(s.Backup.Root)) Block("La sauvegarde n'est plus accessible (support débranché ?).");

        foreach (var p in s.Personal.Where(p => p.Root.State != CheckState.Unchecked && p.DestinationPath is null))
            Block($"{p.Label} : dossier de destination introuvable sur ce PC.");
        foreach (var a in s.Additional.Where(a => a.Selected && !a.HasDestination))
            Block($"{a.Item.Name} : destination à définir ({a.ProposalNote}).");
        foreach (var b in s.Browsers.Where(b => b.Selected && b.Target is null))
            Block($"{b.Browser.Name} — {b.Profile.DisplayName} : choisissez le profil de destination.");
        foreach (var g in s.Browsers.Where(b => b.Selected && b.Target != null).GroupBy(b => b.Target!.ProfilePath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Block($"Plusieurs profils sauvegardés seraient restaurés dans le même profil ({g.First().Target!.Label}) : choisissez des destinations différentes.");

        foreach (var item in plan.Items)
        {
            if (!(item.IsFile ? File.Exists(item.SourcePath) : Directory.Exists(item.SourcePath)))
                Block($"{item.Label} : élément absent de la sauvegarde ({item.RelativeSourcePath}).");
            if (!RestorePaths.IsInside(item.SourcePath, s.Backup.Root))
                Block($"{item.Label} : source hors de la sauvegarde, refusée par sécurité.");
            if (RestorePaths.NormalizeAbsolute(item.DestinationPath) is null)
                Block($"{item.Label} : destination invalide ({item.DestinationPath}).");
            else if (RestorePaths.IsProtectedLocation(item.DestinationPath))
                Block($"{item.Label} : destination dans un dossier système, refusée ({item.DestinationPath}).");
            else if (s.Target.Profile.IsInOtherUserProfile(item.DestinationPath))
                Block($"{item.Label} : destination dans le profil d'un autre utilisateur, refusée.");
            if (RestorePaths.IsInside(item.DestinationPath, s.Backup.Root) || RestorePaths.IsInside(s.Backup.Root, item.DestinationPath))
                Block($"{item.Label} : la destination et la sauvegarde se recouvrent ({item.DestinationPath}). Placez la sauvegarde ailleurs.");
            else if (!DestinationReachable(item.DestinationPath))
                Block($"{item.Label} : destination inaccessible ({item.DestinationPath}).");
        }

        if (plan.IsEmpty && issues.All(i => !i.IsBlocking)) Block("Rien n'est sélectionné.");

        if (plan.Wifi.Count > 0 && s.WifiState is { Available: false } ws)
            Warn(ws.Message ?? "Wi-Fi indisponible : les profils ne pourront pas être importés.");
        if (s.Backup.IsLegacy)
            Warn("Sauvegarde ancienne génération : les chemins d'origine sont lus dans copy-log.txt quand ils y figurent.");
        if (plan.Items.Any(i => i.DestinationIsOneDrive))
            Warn("Les fichiers restaurés dans un dossier OneDrive seront ensuite synchronisés par OneDrive.");
        if (s.Backup.Source.Status is "cancelled" or "failed")
            Warn("La sauvegarde source est incomplète (copie interrompue) : seuls les éléments présents seront restaurés.");

        if (analysis != null)
        {
            foreach (var v in analysis.Volumes.Where(v => !v.Sufficient))
                Block($"Espace insuffisant sur {v.Root.TrimEnd('\\')} : {Format.Size(v.NeededBytes)} nécessaires, {Format.Size(v.FreeBytes ?? 0)} disponibles.");
        }
        return issues;
    }

    /// <summary>The destination exists, or its nearest existing ancestor does (folders are created during the restore).</summary>
    private static bool DestinationReachable(string path) => NearestExisting(path) != null;

    internal static string? NearestExisting(string path)
    {
        try
        {
            var current = PathUtil.Normalize(path);
            while (current != null)
            {
                if (Directory.Exists(current)) return current;
                current = PathUtil.Parent(current);
            }
        }
        catch
        {
            // invalid path
        }
        return null;
    }

    /// <summary>
    /// Pre-scan with the engine's rules: compares the selected backup files with the destination by name, size and date
    /// (no hash), and computes the space needed per volume.
    /// </summary>
    public static RestoreAnalysis Analyze(RestorePlan plan, CancellationToken ct = default, Action<long>? onFiles = null)
    {
        var analysis = new RestoreAnalysis { WifiCount = plan.Wifi.Count, HasWallpaper = plan.Wallpaper != null };
        var needs = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long seen = 0;

        void Need(string path, long bytes)
        {
            if (bytes <= 0) return;
            var existing = NearestExisting(path);
            if (existing is null) return;
            var root = VolumeService.GetVolumeRoot(existing) ?? Path.GetPathRoot(existing) ?? existing;
            needs[root] = needs.GetValueOrDefault(root) + bytes;
        }

        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            var a = new RestoreItemAnalysis();
            long needed = 0, safety = 0;
            foreach (var e in RestoreWalker.Walk(item, ct))
            {
                switch (e.Kind)
                {
                    case WalkKind.Skipped: a.Skipped++; continue;
                    case WalkKind.Error: a.Errors++; continue;
                    case WalkKind.EnterDirectory: continue;
                }
                var size = e.Source!.Size;
                a.Files++;
                a.Bytes += size;
                if (onFiles != null && ++seen % 2000 == 0) onFiles(seen);
                switch (ConflictResolver.Decide(item.Policy, size, e.Source.LastWriteUtc, e.Existing))
                {
                    case FileDecision.Restore:
                        a.NewFiles++;
                        a.NewBytes += size;
                        needed += size;
                        break;
                    case FileDecision.Replace:
                        a.ReplaceFiles++;
                        a.ReplaceBytes += size;
                        a.ReplacedExistingBytes += e.Existing!.Size;
                        // A replacement is written beside the old file first, then swapped: the new size is needed.
                        needed += size;
                        if (item.SafetyFolder != null) safety += e.Existing.Size;
                        break;
                    case FileDecision.Keep: a.KeepFiles++; break;
                    case FileDecision.Identical: a.IdenticalFiles++; break;
                }
            }
            analysis.PerItem[item] = a;
            Need(item.DestinationPath, needed);
            if (item.SafetyFolder != null) Need(item.SafetyFolder, safety);
        }

        if (plan.Wallpaper is { } wp && File.Exists(wp.SourcePath))
        {
            var size = new FileInfo(wp.SourcePath).Length;
            Need(wp.TranscodedPath, size);
            Need(wp.StoreFolder, size);
        }

        foreach (var (root, bytes) in needs)
            analysis.Volumes.Add(new VolumeNeed(root, bytes, VolumeService.GetFreeSpace(root)?.Free));
        return analysis;
    }

    /// <summary>
    /// Size estimate per personal folder / element, with the backup workflow's calculator (cached sizes, never blocking
    /// the UI): copy jobs reading the backup, used only to show selected sizes.
    /// </summary>
    public static IReadOnlyList<CopyJob> EstimateJobs(RestorePlan plan) => plan.Items.Select(i => new CopyJob
    {
        Category = i.Category,
        Label = i.Label,
        SourcePath = i.SourcePath,
        DestinationRelative = i.RelativeSourcePath,
        IsFile = i.IsFile,
        Selection = i.Selection,
    }).ToList();
}
