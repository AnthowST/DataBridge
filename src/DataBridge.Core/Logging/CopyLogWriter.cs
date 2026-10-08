using System.Text;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;

namespace DataBridge.Core.Logging;

/// <summary>
/// Writes copy-log.txt. Issues are streamed to temporary section files while copying (nothing accumulates in
/// memory, and the trace survives a crash), then assembled after the summary. The log contains paths and
/// counters only: never file contents, browser passwords, Wi-Fi keys or other secrets.
/// </summary>
public sealed class CopyLogWriter
{
    public const string FileName = "copy-log.txt";

    private readonly string _root;
    private readonly Dictionary<string, StreamWriter> _sections = new();
    private static readonly string[] SectionOrder = ["errors", "ignored", "cloud", "info"];

    public CopyLogWriter(string backupRoot)
    {
        _root = backupRoot;
    }

    private string TempPath(string section) => Path.Combine(_root, $".copy-log-{section}.tmp");

    public void Write(CopyIssue issue)
    {
        var section = issue.Kind == IssueKind.CloudOnly && issue.Severity != IssueSeverity.Error ? "cloud"
            : issue.Severity switch { IssueSeverity.Error => "errors", IssueSeverity.Ignored => "ignored", _ => "info" };
        try
        {
            if (!_sections.TryGetValue(section, out var writer))
            {
                writer = new StreamWriter(TempPath(section), false, new UTF8Encoding(false)) { AutoFlush = false };
                _sections[section] = writer;
            }
            writer.WriteLine($"[{issue.Time:HH:mm:ss}] {issue.KindLabel} : {issue.Path}");
            writer.WriteLine($"           {issue.Message}");
        }
        catch
        {
            // Logging must never stop the copy (e.g. destination full: the fatal error is reported anyway).
        }
    }

    public string Complete(CopyPlan plan, CopyResult result)
    {
        foreach (var w in _sections.Values)
        {
            try { w.Dispose(); } catch { /* ignore */ }
        }

        var path = Path.Combine(_root, FileName);
        using (var log = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            WriteHeader(log, plan, result);
            WriteSummary(log, plan, result);
            WriteOneDrive(log, plan);
            WriteBrowserEvents(log, plan);
            WriteJobs(log, plan, result);
            WriteWifi(log, result);
            WritePrinters(log, plan, result);
            WriteOutlookMailbox(log, plan, result);
            WriteSection(log, "ÉLÉMENTS IGNORÉS", "ignored", result.SkippedFileCount + result.InterruptedFileCount);
            WriteSection(log, "FICHIERS DISPONIBLES UNIQUEMENT EN LIGNE (OneDrive / cloud) — NON TÉLÉCHARGÉS, NON COPIÉS", "cloud", result.SkippedCloudOnlyCount);
            WriteSection(log, "ERREURS", "errors", result.ErrorCount);
            WriteSection(log, "INFORMATIONS", "info", result.CopiedWhileOpenCount);
            log.WriteLine();
            log.WriteLine("Fin du journal.");
        }

        foreach (var section in SectionOrder)
        {
            try { File.Delete(TempPath(section)); } catch { /* ignore */ }
        }
        return path;
    }

    private static void Title(StreamWriter log, string title)
    {
        log.WriteLine();
        log.WriteLine(new string('-', 78));
        log.WriteLine(" " + title);
        log.WriteLine(new string('-', 78));
    }

    private static void WriteHeader(StreamWriter log, CopyPlan plan, CopyResult result)
    {
        var p = plan.Profile;
        log.WriteLine(new string('=', 78));
        log.WriteLine($" {AppInfo.Name} — {BackupNaming.LogSignature}");
        log.WriteLine(new string('=', 78));
        log.WriteLine($"Version           : {AppInfo.Name} {AppInfo.Version}");
        log.WriteLine($"Date              : {result.StartedAt:dd.MM.yyyy HH:mm:ss}");
        log.WriteLine($"PC                : {p.MachineName}");
        log.WriteLine($"Session           : {p.UserName} (domaine {p.UserDomain})");
        log.WriteLine($"Profil            : {p.ProfilePath}");
        log.WriteLine($"Windows           : {p.WindowsVersion}");
        log.WriteLine($"Droits            : {p.ElevationLabel}");
        log.WriteLine($"Destination       : {result.BackupPath}");
        var status = result.Outcome switch
        {
            CopyOutcome.Completed => "Sauvegarde terminée",
            CopyOutcome.Cancelled => "Copie interrompue par l'utilisateur",
            _ => "Copie interrompue : " + (result.FatalMessage ?? "erreur critique"),
        };
        log.WriteLine($"Statut            : {status}");
        log.WriteLine($"Durée             : {Format.Duration(result.Duration)}");
        log.WriteLine($"Fichiers en ligne : {(plan.SkipCloudOnly ? "non téléchargés (ignorés)" : "téléchargés puis copiés")}");
        log.WriteLine($"Manifest          : {(result.ManifestPath != null ? Restore.BackupManifest.FileName : "non écrit — " + (result.ManifestError ?? "erreur inconnue"))}");
    }

    private static void WriteSummary(StreamWriter log, CopyPlan plan, CopyResult result)
    {
        Title(log, "RÉSUMÉ");
        void Line(string label, string value) => log.WriteLine($"{label,-38} : {value}");

        Line("Fichiers sélectionnés", $"{Format.Count(result.SelectedFileCount)} ({Format.Size(result.SelectedBytes)})");
        Line("Fichiers prévus pour la copie", $"{Format.Count(result.PlannedCopyFileCount)} ({Format.Size(result.PlannedCopyBytes)})");
        Line("Fichiers copiés", Format.Count(result.CopiedFileCount));
        Line("Données copiées", Format.Size(result.CopiedBytes));
        Line("Fichiers cloud non téléchargés", $"{Format.Count(result.SkippedCloudOnlyCount)} ({Format.Size(result.SkippedCloudOnlyBytes)})");
        Line("Éléments ignorés", $"{Format.Count(result.SkippedFileCount)} (liens / jonctions non suivis)");
        Line("Fichiers en erreur", $"{Format.Count(result.ErrorFileCount)} (dont verrouillés : {Format.Count(result.LockedFileCount)})");
        Line("Dossiers en erreur (lecture/création)", Format.Count(result.ErrorFolderCount));
        if (result.InterruptedFileCount > 0)
            Line("Fichiers interrompus (annulation)", Format.Count(result.InterruptedFileCount));
        if (result.NotProcessedFileCount > 0)
            Line("Fichiers non traités (copie arrêtée)", Format.Count(result.NotProcessedFileCount));
        Line("Doublons évités", Format.Count(result.DuplicateCount));
        if (result.CopiedWhileOpenCount > 0)
            Line("Copiés alors qu'ouverts", Format.Count(result.CopiedWhileOpenCount));
        Line("Contrôle", ControlText(result));
        if (plan.Wifi != null)
            Line("Profils Wi-Fi exportés", $"{result.WifiExported} / {plan.Wifi.Profiles.Count} (clés incluses : {result.WifiKeysIncluded}, " +
                                         $"clés protégées par Windows : {result.WifiKeysProtected}, sans clé partagée : {result.WifiWithoutKey})");
        if (result.Outcome == CopyOutcome.Cancelled)
            log.WriteLine("Les éléments déjà copiés ont été conservés.");
    }

    /// <summary>Planned = copied + errors (+ interrupted + not processed), or the drift when the source changed meanwhile.</summary>
    public static string ControlText(CopyResult r)
    {
        if (r.Drift != 0)
            return $"prévus {Format.Count(r.PlannedCopyFileCount)}, traités {Format.Count(r.ProcessedFileCount)} " +
                   $"(écart de {Format.Count(Math.Abs(r.Drift))} : fichiers créés ou supprimés sur la source pendant la copie)";
        var text = $"prévus {Format.Count(r.PlannedCopyFileCount)} = copiés {Format.Count(r.CopiedFileCount)} + en erreur {Format.Count(r.ErrorFileCount)}";
        if (r.InterruptedFileCount > 0) text += $" + interrompus {Format.Count(r.InterruptedFileCount)}";
        if (r.NotProcessedFileCount > 0) text += $" + non traités {Format.Count(r.NotProcessedFileCount)}";
        return text;
    }

    private static void WriteOneDrive(StreamWriter log, CopyPlan plan)
    {
        if (plan.OneDriveLines.Count == 0) return;
        Title(log, "ONEDRIVE");
        foreach (var line in plan.OneDriveLines) log.WriteLine(line);
    }

    private static void WriteBrowserEvents(StreamWriter log, CopyPlan plan)
    {
        if (plan.BrowserEvents.Count == 0) return;
        Title(log, "NAVIGATEURS");
        foreach (var line in plan.BrowserEvents) log.WriteLine(line);
    }

    private static void WriteJobs(StreamWriter log, CopyPlan plan, CopyResult result)
    {
        Title(log, "ÉLÉMENTS COPIÉS (par sélection)");
        foreach (var job in plan.Jobs)
        {
            var jr = result.Jobs.FirstOrDefault(j => j.Job == job);
            log.WriteLine();
            log.WriteLine($"[{job.Category}] {job.Label}");
            log.WriteLine($"  Source originale : {job.SourcePath}");
            log.WriteLine($"  Destination      : {job.DestinationRelative}");
            if (jr is null)
            {
                log.WriteLine("  Résultat         : non traité (copie arrêtée avant cet élément)");
            }
            else
            {
                var extra = new List<string>();
                if (jr.CloudSkipped > 0) extra.Add(Format.Plural(jr.CloudSkipped, "fichier cloud non téléchargé", "fichiers cloud non téléchargés"));
                if (jr.ErrorFiles > 0) extra.Add(Format.Plural(jr.ErrorFiles, "fichier en erreur", "fichiers en erreur"));
                if (jr.ErrorFolders > 0) extra.Add(Format.Plural(jr.ErrorFolders, "dossier en erreur", "dossiers en erreur"));
                if (jr.Skipped > 0) extra.Add(Format.Plural(jr.Skipped, "lien ignoré", "liens ignorés"));
                if (jr.Interrupted > 0) extra.Add(Format.Plural(jr.Interrupted, "fichier interrompu", "fichiers interrompus"));
                if (jr.Duplicates > 0) extra.Add(Format.Plural(jr.Duplicates, "doublon évité", "doublons évités"));
                log.WriteLine($"  Résultat         : {Format.Plural(jr.CopiedFiles, "fichier copié", "fichiers copiés")}, {Format.Size(jr.CopiedBytes)}"
                              + (extra.Count > 0 ? " — " + string.Join(", ", extra) : ""));
            }

            if (job.Selection is { } selection && job.Category is Categories.Personal)
            {
                var excluded = selection.Rules.Where(r => !r.Value).Select(r => PathUtil.Relative(r.Key, selection.RootPath)).OrderBy(s => s).ToList();
                var included = selection.Rules.Where(r => r.Value).Select(r => PathUtil.Relative(r.Key, selection.RootPath)).OrderBy(s => s).ToList();
                if (excluded.Count > 0)
                {
                    log.WriteLine("  Exclusions manuelles :");
                    foreach (var e in excluded) log.WriteLine($"    - {e}");
                }
                if (included.Count > 0)
                {
                    log.WriteLine("  Inclusions manuelles (dans un dossier exclu) :");
                    foreach (var i in included) log.WriteLine($"    + {i}");
                }
            }
            // The backup folder itself is always excluded: only mention real overlaps with other selections.
            var overlaps = job.Coverage?.Describe().Split("; ").Where(d => !d.Contains("(dossier de sauvegarde)")).ToList() ?? [];
            if (overlaps.Count > 0)
                log.WriteLine($"  Anti-doublons    : {string.Join("; ", overlaps)}");
            foreach (var note in job.Notes)
                log.WriteLine($"  {note}");
        }

        if (plan.NotSelected.Count > 0)
        {
            log.WriteLine();
            log.WriteLine("Non copiés (choix du technicien / déjà couverts) :");
            foreach (var line in plan.NotSelected) log.WriteLine($"  - {line}");
        }
    }

    private static void WriteWifi(StreamWriter log, CopyResult result)
    {
        Title(log, "WI-FI");
        if (result.Wifi.Count == 0)
        {
            log.WriteLine("Aucun profil Wi-Fi exporté.");
            return;
        }
        foreach (var w in result.Wifi)
        {
            if (!w.Exported)
            {
                log.WriteLine($"{w.ProfileName} : ÉCHEC de l'export — {w.Error}");
                continue;
            }
            var key = w.KeyStatus switch
            {
                WifiKeyStatus.Available => "clé incluse",
                WifiKeyStatus.NoKey => "pas de clé partagée (réseau ouvert / entreprise)",
                WifiKeyStatus.NotAccessible => "clé NON incluse (protégée par Windows, non accessible avec les droits de la session)",
                _ => "état de la clé inconnu",
            };
            log.WriteLine($"{w.ProfileName} : profil exporté ({w.FileName}) — {key}");
        }
        log.WriteLine("Les clés Wi-Fi ne sont jamais écrites dans ce journal. Les fichiers XML exportés peuvent les contenir : à conserver en lieu sûr.");
    }

    private static void WritePrinters(StreamWriter log, CopyPlan plan, CopyResult result)
    {
        Title(log, "IMPRIMANTES");
        if (plan.Printers is null)
        {
            log.WriteLine("Aucune imprimante sélectionnée.");
            return;
        }
        if (result.Printers is null)
        {
            log.WriteLine("Non traité (copie arrêtée avant cette étape).");
            return;
        }
        foreach (var line in result.Printers.LogLines()) log.WriteLine(line);
        log.WriteLine("Export en lecture seule, avec les droits de la session. Aucune page de test n'est imprimée.");
    }

    private static void WriteOutlookMailbox(StreamWriter log, CopyPlan plan, CopyResult result)
    {
        Title(log, "OUTLOOK — BOÎTE AUX LETTRES");
        if (plan.Outlook is null)
        {
            log.WriteLine("Aucune boîte aux lettres sélectionnée (signatures seulement, si incluses).");
            return;
        }
        if (result.Outlook is null)
        {
            log.WriteLine("Non traité (copie arrêtée avant cette étape).");
            return;
        }
        foreach (var line in result.Outlook.LogLines()) log.WriteLine(line);
        log.WriteLine("La boîte source n'est que lue : rien n'y est déplacé, supprimé ni envoyé.");
    }

    private void WriteSection(StreamWriter log, string title, string section, int count)
    {
        Title(log, $"{title} ({Format.Count(count)})");
        var temp = TempPath(section);
        if (!File.Exists(temp))
        {
            log.WriteLine("Aucun.");
            return;
        }
        using var reader = new StreamReader(temp, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null) log.WriteLine(line);
    }
}
