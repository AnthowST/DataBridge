using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Logging;
using DataBridge.Core.Profile;

namespace DataBridge.Core.Restore;

/// <summary>
/// Opens a folder chosen by the technician and decides whether it is a DataBridge backup.
/// 2.0+ backups are read from databridge-manifest.json (refused cleanly when written with a newer schema);
/// 1.x backups (DataBridge_, itelium_ProfileCopy_, ProfileCopy_) are reconstructed from their folder structure and
/// copy-log.txt. The folder name alone is never enough. Every path found is checked to stay inside the backup.
/// </summary>
public static class BackupReader
{
    public static BackupOpenResult Open(string folder)
    {
        string root;
        try
        {
            root = PathUtil.Normalize(folder);
        }
        catch (Exception ex)
        {
            return BackupOpenResult.Fail("Chemin invalide : " + ex.Message);
        }
        if (!Directory.Exists(root)) return BackupOpenResult.Fail("Dossier introuvable ou inaccessible avec les droits de la session.");

        var manifestPath = Path.Combine(root, BackupManifest.FileName);
        string? manifestWarning = null;
        if (File.Exists(manifestPath))
        {
            var (manifest, error, future) = ReadManifest(manifestPath);
            if (future) return BackupOpenResult.Fail(error!);
            if (manifest != null) return FromManifest(root, manifest);
            manifestWarning = $"Le fichier {BackupManifest.FileName} est illisible ({error}) : la sauvegarde est lue comme une sauvegarde ancienne génération.";
        }

        if (!BackupNaming.LooksLikeBackup(root) && !HasLegacyStructure(root))
        {
            var candidates = FindCandidates(root);
            return BackupOpenResult.Fail(candidates.Count > 0
                ? "Ce dossier contient des sauvegardes DataBridge mais n'en est pas une : sélectionnez le dossier de la sauvegarde elle-même."
                : "Ce dossier n'est pas une sauvegarde DataBridge (ni databridge-manifest.json, ni copy-log.txt de DataBridge, ni structure reconnue).",
                candidates);
        }

        var content = FromLegacy(root, manifestWarning);
        return content.IsEmpty
            ? BackupOpenResult.Fail("Sauvegarde reconnue, mais elle ne contient aucun élément restaurable.")
            : new BackupOpenResult(content, null, []);
    }

    // ------------------------------------------------------------------ manifest (2.0+)

    /// <summary>Reads and checks the manifest. <c>future</c> = written by a newer DataBridge with an unsupported schema.</summary>
    internal static (BackupManifest? Manifest, string? Error, bool Future) ReadManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                if (!doc.RootElement.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var schema))
                    return (null, "schemaVersion absent", false);
                if (schema > BackupManifest.CurrentSchemaVersion)
                    return (null, "Cette sauvegarde utilise une version de format plus récente que cette version de DataBridge. " +
                                  "Mettez DataBridge à jour pour la restaurer.", true);
                if (schema < 1) return (null, "schemaVersion invalide", false);
            }
            var manifest = JsonSerializer.Deserialize<BackupManifest>(json, BackupManifest.JsonOptions);
            return manifest is null ? (null, "contenu vide", false) : (manifest, null, false);
        }
        catch (Exception ex)
        {
            return (null, ex.Message, false);
        }
    }

    private static BackupOpenResult FromManifest(string root, BackupManifest m)
    {
        var warnings = new List<string>();
        var security = new List<string>();

        string? Resolve(string? relative, string what, bool isFile)
        {
            var full = RestorePaths.ResolveInside(root, relative);
            if (full is null)
            {
                security.Add($"{what} : chemin refusé ({relative ?? "vide"}) — il sortirait de la sauvegarde ou n'est pas un chemin relatif sûr.");
                return null;
            }
            var entry = FastDirectory.GetEntry(full);
            if (entry is null)
            {
                warnings.Add($"{what} : absent de la sauvegarde ({relative}).");
                return null;
            }
            if (entry.IsLink)
            {
                security.Add($"{what} : lien / jonction dans la sauvegarde ({relative}) — jamais suivi.");
                return null;
            }
            if (entry.IsDirectory == isFile)
            {
                warnings.Add($"{what} : type inattendu dans la sauvegarde ({relative}).");
                return null;
            }
            return full;
        }

        var personal = new List<BackupPersonalFolder>();
        foreach (var p in m.PersonalFolders)
        {
            if (!Enum.TryParse<PersonalFolderKind>(p.Kind, true, out var kind))
            {
                warnings.Add($"Dossier personnel inconnu ignoré : {p.Kind}.");
                continue;
            }
            if (personal.Any(x => x.Kind == kind)) continue;
            var def = KnownFolders.Personal.First(d => d.Kind == kind);
            if (Resolve(p.RelativePath, def.DisplayName, false) is not { } full) continue;
            personal.Add(new BackupPersonalFolder(kind, def.DisplayName, p.RelativePath, full, Original(p.OriginalPath), p.Partial, p.Files, p.Bytes, p.Location));
        }

        var additional = new List<BackupAdditionalItem>();
        foreach (var a in m.AdditionalItems)
        {
            if (Resolve(a.RelativePath, "Élément supplémentaire " + a.Name, a.IsFile) is not { } full) continue;
            additional.Add(new BackupAdditionalItem(Path.GetFileName(full), a.RelativePath, full, a.IsFile, Original(a.OriginalPath), a.Files, a.Bytes));
        }

        BackupSignatures? signatures = null;
        if (m.OutlookSignatures is { } s && Resolve(s.RelativePath, "Signatures Outlook", false) is { } sigFull)
            signatures = new BackupSignatures(s.RelativePath, sigFull, Original(s.OriginalPath), s.Names);

        var browsers = new List<BackupBrowser>();
        foreach (var b in m.Browsers)
        {
            if (!Enum.TryParse<BrowserKind>(b.Browser, true, out var kind)) continue;
            var profiles = new List<BackupBrowserProfile>();
            foreach (var p in b.Profiles)
            {
                if (Resolve(p.RelativePath, $"{b.Name} — profil {p.Folder}", false) is not { } pf) continue;
                profiles.Add(new BackupBrowserProfile(Path.GetFileName(pf), string.IsNullOrWhiteSpace(p.DisplayName) ? p.Folder : p.DisplayName,
                    string.IsNullOrWhiteSpace(p.Account) ? null : p.Account, p.IsActive, p.RelativePath, pf));
            }
            var bRoot = RestorePaths.ResolveInside(root, b.RelativePath) ?? (profiles.Count > 0 ? PathUtil.Parent(profiles[0].FullPath)! : root);
            if (profiles.Count > 0) browsers.Add(new BackupBrowser(kind, b.Name, b.RelativePath, bRoot, profiles));
        }

        var wifi = new List<BackupWifiProfile>();
        foreach (var w in m.Wifi)
        {
            if (Resolve(w.File, "Profil Wi-Fi " + w.Name, true) is not { } wf) continue;
            wifi.Add(new BackupWifiProfile(w.Name, w.File, wf, w.Authentication ?? "Inconnue", w.KeyIncluded,
                w.Scope is null ? null : w.Scope.Equals("currentUser", StringComparison.OrdinalIgnoreCase)));
        }

        BackupWallpaper? wallpaper = null;
        if (m.Wallpaper is { } wp && Resolve(wp.RelativePath, "Fond d'écran", true) is { } wpFull)
            wallpaper = new BackupWallpaper(wp.RelativePath, wpFull, new FileInfo(wpFull).Length);

        if (m.Status is "cancelled") warnings.Add("Cette sauvegarde a été interrompue par l'utilisateur : elle peut être incomplète.");
        if (m.Status is "failed") warnings.Add("Cette sauvegarde s'est arrêtée sur une erreur : elle peut être incomplète.");

        var content = new BackupContent
        {
            Root = root,
            Generation = BackupGeneration.Manifest,
            SchemaVersion = m.SchemaVersion,
            BackupId = m.BackupId,
            Source = new BackupSourceInfo(m.Source.Computer, m.Source.User, Original(m.Source.ProfilePath), m.Source.WindowsVersion,
                m.CreatedAt == default ? null : m.CreatedAt.LocalDateTime, m.DataBridgeVersion, AppInfo.Name, m.Status),
            TotalFiles = m.Totals.Files,
            TotalBytes = m.Totals.Bytes,
            SkipCloudOnly = m.Options.SkipCloudOnly,
            Personal = personal.OrderBy(p => p.Kind).ToList(),
            Additional = additional,
            Signatures = signatures,
            Browsers = browsers,
            Wifi = wifi,
            Wallpaper = wallpaper,
            Warnings = warnings,
            SecurityIssues = security,
        };
        return content.IsEmpty && security.Count == 0
            ? BackupOpenResult.Fail("Sauvegarde reconnue, mais elle ne contient aucun élément restaurable.")
            : new BackupOpenResult(content, null, []);
    }

    /// <summary>An original path from the manifest/log is only kept when it is a clean absolute path (display + proposal).</summary>
    private static string? Original(string? path) => RestorePaths.NormalizeAbsolute(path);

    // ------------------------------------------------------------------ legacy (1.x)

    private static readonly string[] LegacyFolders = ["Personal", "Additional", "Outlook", "Browsers", "Network", "Windows"];

    private static bool HasLegacyStructure(string root)
    {
        // Without log nor known prefix, at least two of the DataBridge folders and nothing that looks unrelated.
        var known = LegacyFolders.Count(f => Directory.Exists(Path.Combine(root, f)));
        return known >= 2 && Directory.Exists(Path.Combine(root, "Personal"));
    }

    private static List<string> FindCandidates(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, BackupManifest.FileName)) || BackupNaming.LooksLikeBackup(d))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static BackupContent FromLegacy(string root, string? extraWarning)
    {
        var warnings = new List<string>();
        var security = new List<string>();
        if (extraWarning != null) warnings.Add(extraWarning);
        var log = LegacyLog.Read(Path.Combine(root, CopyLogWriter.FileName));
        if (log is null) warnings.Add("copy-log.txt absent ou illisible : PC, session et chemins d'origine inconnus.");
        else warnings.Add("Sauvegarde DataBridge ancienne génération : certaines informations avancées peuvent être indisponibles.");

        string? Child(string relative, bool isFile, string what)
        {
            var full = RestorePaths.ResolveInside(root, relative);
            if (full is null) return null;
            var entry = FastDirectory.GetEntry(full);
            if (entry is null || entry.IsDirectory == isFile) return null;
            if (entry.IsLink)
            {
                security.Add($"{what} : lien / jonction dans la sauvegarde ({relative}) — jamais suivi.");
                return null;
            }
            return full;
        }

        var personal = new List<BackupPersonalFolder>();
        foreach (var def in KnownFolders.Personal)
        {
            var relative = @"Personal\" + def.DestinationName;
            if (Child(relative, false, def.DisplayName) is not { } full) continue;
            var job = log?.JobByDestination(relative);
            var partial = job?.HasManualExclusions ?? false;
            personal.Add(new BackupPersonalFolder(def.Kind, def.DisplayName, relative, full, Original(job?.Source), partial, null, null, null));
        }

        var additional = new List<BackupAdditionalItem>();
        var additionalRoot = Path.Combine(root, "Additional");
        if (Directory.Exists(additionalRoot) && FastDirectory.TryList(additionalRoot, out _) is { } entries)
        {
            foreach (var e in entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var relative = @"Additional\" + e.Name;
                if (e.IsLink)
                {
                    security.Add($"Élément supplémentaire {e.Name} : lien / jonction dans la sauvegarde — jamais suivi.");
                    continue;
                }
                if (!RestorePaths.IsSafeName(e.Name)) continue;
                var job = log?.JobByDestination(relative);
                additional.Add(new BackupAdditionalItem(e.Name, relative, e.FullPath, !e.IsDirectory, Original(job?.Source), null, e.IsDirectory ? null : e.Size));
            }
        }

        BackupSignatures? signatures = null;
        if (Child(@"Outlook\Signatures", false, "Signatures Outlook") is { } sigFull)
            signatures = new BackupSignatures(@"Outlook\Signatures", sigFull, Original(log?.JobByDestination(@"Outlook\Signatures")?.Source), SignatureNames(sigFull));

        var browsers = new List<BackupBrowser>();
        foreach (var (kind, folder) in new[] { (BrowserKind.Chrome, "Chrome"), (BrowserKind.Edge, "Edge"), (BrowserKind.Firefox, "Firefox") })
        {
            var b = ReadLegacyBrowser(root, kind, folder, security);
            if (b != null) browsers.Add(b);
        }

        var wifi = new List<BackupWifiProfile>();
        var wifiRoot = Path.Combine(root, @"Network\WiFi");
        if (Directory.Exists(wifiRoot) && FastDirectory.TryList(wifiRoot, out _) is { } wifiFiles)
        {
            foreach (var f in wifiFiles.Where(f => !f.IsDirectory && !f.IsLink && f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                var info = WifiRestoreService.ReadProfileFile(f.FullPath);
                if (info is null) continue;
                wifi.Add(new BackupWifiProfile(info.Value.Name, @"Network\WiFi\" + f.Name, f.FullPath, info.Value.Authentication, info.Value.KeyIncluded, null));
            }
        }

        BackupWallpaper? wallpaper = null;
        if (Child(@"Windows\Wallpaper\TranscodedWallpaper", true, "Fond d'écran") is { } wpFull)
            wallpaper = new BackupWallpaper(@"Windows\Wallpaper\TranscodedWallpaper", wpFull, new FileInfo(wpFull).Length);

        var product = log?.Product;
        return new BackupContent
        {
            Root = root,
            Generation = BackupGeneration.Legacy,
            Source = new BackupSourceInfo(log?.Get("PC"), log?.User, Original(log?.Get("Profil")), log?.Get("Windows"), log?.Date,
                log?.Version, product, log?.Get("Statut")),
            SkipCloudOnly = log?.Get("Fichiers en ligne")?.StartsWith("non", StringComparison.OrdinalIgnoreCase) ?? true,
            Personal = personal,
            Additional = additional,
            Signatures = signatures,
            Browsers = browsers,
            Wifi = wifi,
            Wallpaper = wallpaper,
            Warnings = warnings,
            SecurityIssues = security,
        };
    }

    private static List<string> SignatureNames(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => f.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase)
                            || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static BackupBrowser? ReadLegacyBrowser(string root, BrowserKind kind, string folder, List<string> security)
    {
        var name = BrowserProcessService.DisplayName(kind);
        var profiles = new List<BackupBrowserProfile>();
        string relativeRoot;
        string fullRoot;

        if (kind == BrowserKind.Firefox)
        {
            relativeRoot = @"Browsers\Firefox";
            fullRoot = Path.Combine(root, relativeRoot);
            if (!Directory.Exists(fullRoot) || FastDirectory.GetEntry(fullRoot) is { IsLink: true }) return null;
            var info = BrowserDetector.DetectFirefox(fullRoot);
            foreach (var p in info.Profiles)
            {
                // Absolute profile paths of profiles.ini point to the original PC: only folders really inside the backup count.
                if (!RestorePaths.IsInside(p.FullPath, fullRoot) || !Directory.Exists(p.FullPath)) continue;
                if (FastDirectory.GetEntry(p.FullPath) is { IsLink: true }) { security.Add($"{name} : profil {p.FolderName} est un lien — jamais suivi."); continue; }
                profiles.Add(new BackupBrowserProfile(p.FolderName, p.DisplayName, null, p.IsActive,
                    Path.Combine(relativeRoot, PathUtil.Relative(p.FullPath, fullRoot)), p.FullPath));
            }
            // Profiles directory entries not referenced by the copied profiles.ini.
            var profilesDir = Path.Combine(fullRoot, "Profiles");
            if (FastDirectory.TryList(profilesDir, out _) is { } dirs)
                foreach (var d in dirs.Where(d => d.IsDirectory && !d.IsLink && profiles.All(p => !PathUtil.AreSame(p.FullPath, d.FullPath))))
                    profiles.Add(new BackupBrowserProfile(d.Name, d.Name, null, false, @"Browsers\Firefox\Profiles\" + d.Name, d.FullPath));
        }
        else
        {
            relativeRoot = $@"Browsers\{folder}\User Data";
            fullRoot = Path.Combine(root, relativeRoot);
            if (Directory.Exists(fullRoot) && FastDirectory.GetEntry(fullRoot) is { IsLink: false })
            {
                var info = BrowserDetector.DetectChromium(kind, name, BrowserProcessService.ProcessName(kind), fullRoot);
                foreach (var p in info.Profiles.Where(p => RestorePaths.IsInside(p.FullPath, fullRoot) && Directory.Exists(p.FullPath)))
                {
                    if (FastDirectory.GetEntry(p.FullPath) is { IsLink: true }) { security.Add($"{name} : profil {p.FolderName} est un lien — jamais suivi."); continue; }
                    profiles.Add(new BackupBrowserProfile(p.FolderName, p.DisplayName, p.Account, p.IsActive,
                        Path.Combine(relativeRoot, p.FolderName), p.FullPath));
                }
            }
            else
            {
                fullRoot = Path.Combine(root, $@"Browsers\{folder}");
            }
        }

        // Profiles copied from outside the browser's data folder (1.x: Browsers\<browser>\Profils-externes\<profil>).
        var externalRoot = Path.Combine(root, $@"Browsers\{folder}\Profils-externes");
        if (FastDirectory.TryList(externalRoot, out _) is { } externals)
            foreach (var d in externals.Where(d => d.IsDirectory && !d.IsLink))
                profiles.Add(new BackupBrowserProfile(d.Name, d.Name + " (profil externe)", null, false, $@"Browsers\{folder}\Profils-externes\" + d.Name, d.FullPath));

        return profiles.Count == 0 ? null : new BackupBrowser(kind, name, relativeRoot, fullRoot, profiles);
    }

    /// <summary>The parts of a 1.x copy-log.txt needed to restore: header, copied elements and Wi-Fi exports.</summary>
    internal sealed class LegacyLog
    {
        private readonly Dictionary<string, string> _header = new(StringComparer.OrdinalIgnoreCase);
        public List<LegacyJob> Jobs { get; } = [];
        public string? Product { get; private set; }

        public string? Get(string key) => _header.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        public string? Version => Get("Version") is { } v ? v.Split(' ').LastOrDefault() : null;
        public string? User => Get("Session") is { } s ? s.Split(" (domaine", 2)[0].Trim() : null;
        public DateTime? Date => Get("Date") is { } d && DateTime.TryParseExact(d, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;

        public LegacyJob? JobByDestination(string relative) =>
            Jobs.FirstOrDefault(j => j.Destination != null && string.Equals(j.Destination.TrimEnd('\\'), relative.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        private static readonly Regex HeaderLine = new(@"^(?<key>[^:]+?)\s+:\s(?<value>.*)$", RegexOptions.Compiled);

        public static LegacyLog? Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                var log = new LegacyLog();
                var inHeader = true;
                string? section = null;
                string? previous = null;
                LegacyJob? job = null;
                var signed = false;
                string? line;
                var lineNumber = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (lineNumber <= 4 && line.Contains(BackupNaming.LogSignature, StringComparison.Ordinal))
                    {
                        signed = true;
                        var dash = line.IndexOf('—');
                        if (dash > 0) log.Product = line[..dash].Trim();
                        continue;
                    }
                    if (line.StartsWith("------", StringComparison.Ordinal))
                    {
                        inHeader = false;
                        previous = line;
                        continue;
                    }
                    if (previous?.StartsWith("------", StringComparison.Ordinal) == true && line.StartsWith(' ') && !line.StartsWith("  ", StringComparison.Ordinal))
                    {
                        section = line.Trim();
                        previous = line;
                        // Issue sections come after Wi-Fi: nothing more is needed (they can be very long).
                        if (section.StartsWith("ÉLÉMENTS IGNORÉS", StringComparison.Ordinal) || section.StartsWith("FICHIERS DISPONIBLES", StringComparison.Ordinal)
                            || section.StartsWith("ERREURS", StringComparison.Ordinal))
                            break;
                        continue;
                    }
                    previous = line;

                    if (inHeader)
                    {
                        var m = HeaderLine.Match(line);
                        if (m.Success) log._header[m.Groups["key"].Value.Trim()] = m.Groups["value"].Value.Trim();
                        continue;
                    }

                    if (section?.StartsWith("ÉLÉMENTS COPIÉS", StringComparison.Ordinal) == true)
                    {
                        if (line.StartsWith('[') && line.IndexOf(']') is > 1 and var close)
                        {
                            job = new LegacyJob(line[1..close], line[(close + 1)..].Trim());
                            log.Jobs.Add(job);
                        }
                        else if (job != null && line.StartsWith("  Source originale", StringComparison.Ordinal))
                            job.Source = AfterColon(line);
                        else if (job != null && line.StartsWith("  Destination", StringComparison.Ordinal))
                            job.Destination = AfterColon(line);
                        else if (job != null && line.StartsWith("  Exclusions manuelles", StringComparison.Ordinal))
                            job.HasManualExclusions = true;
                    }
                }
                return signed ? log : null;
            }
            catch
            {
                return null;
            }
        }

        private static string? AfterColon(string line)
        {
            var i = line.IndexOf(" : ", StringComparison.Ordinal);
            return i < 0 ? null : line[(i + 3)..].Trim();
        }
    }

    internal sealed class LegacyJob(string category, string label)
    {
        public string Category { get; } = category;
        public string Label { get; } = label;
        public string? Source { get; set; }
        public string? Destination { get; set; }
        public bool HasManualExclusions { get; set; }
    }
}
