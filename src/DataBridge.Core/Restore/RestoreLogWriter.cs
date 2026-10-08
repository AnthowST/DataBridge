using System.Text;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;

namespace DataBridge.Core.Restore;

/// <summary>
/// Writes restore-AAAA-MM-JJ_HH-mm.log in %LOCALAPPDATA%\DataBridge\Logs (the backup itself is never modified: it may be
/// on a read-only medium or an archive). Per-file events are streamed to temporary section files while restoring, then
/// assembled after the summary. Paths, names and counters only: never a password, Wi-Fi key, token, cookie or content.
/// </summary>
public sealed class RestoreLogWriter
{
    private readonly string _folder;
    private readonly string _stamp;
    private readonly Dictionary<string, StreamWriter> _sections = new();
    private static readonly string[] SectionOrder = ["errors", "replaced", "kept", "ignored", "info"];

    public RestoreLogWriter(string logsFolder, DateTime startedAt)
    {
        _folder = logsFolder;
        Directory.CreateDirectory(_folder);
        _stamp = $"{startedAt:yyyy-MM-dd_HH-mm}";
        _token = Guid.NewGuid().ToString("N")[..8];
    }

    private readonly string _token;
    private string TempPath(string section) => Path.Combine(_folder, $".restore-{_token}-{section}.tmp");

    public void Write(CopyIssue issue)
    {
        var section = issue.Kind switch
        {
            IssueKind.Replaced => "replaced",
            IssueKind.KeptExisting when issue.Path.StartsWith("Wi-Fi", StringComparison.Ordinal) => "info",
            IssueKind.KeptExisting => "kept",
            _ => issue.Severity switch { IssueSeverity.Error => "errors", IssueSeverity.Ignored => "ignored", _ => "info" },
        };
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
            // The log never stops the restore.
        }
    }

    public string Complete(RestorePlan plan, RestoreResult result)
    {
        foreach (var w in _sections.Values)
        {
            try { w.Dispose(); } catch { /* ignore */ }
        }

        var path = Path.Combine(_folder, $"restore-{_stamp}.log");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(_folder, $"restore-{_stamp}_{n}.log");
        using (var log = new StreamWriter(path, false, new UTF8Encoding(true)))
        {
            WriteHeader(log, plan, result);
            WriteSummary(log, plan, result);
            WriteBrowserEvents(log, plan);
            WriteItems(log, plan, result);
            WriteWifi(log, plan, result);
            WriteWallpaper(log, plan, result);
            WritePrinters(log, plan, result);
            WriteOutlook(log, plan, result);
            WriteSection(log, "ERREURS", "errors", result.ErrorCount);
            WriteSection(log, "FICHIERS REMPLACÉS", "replaced", (int)result.ReplacedFiles);
            WriteSection(log, "FICHIERS EXISTANTS CONSERVÉS", "kept", result.KeptFiles);
            WriteSection(log, "ÉLÉMENTS IGNORÉS (liens, noms refusés, annulation)", "ignored", result.SkippedCount + result.InterruptedFiles);
            WriteSection(log, "INFORMATIONS", "info", -1);
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

    private static void Line(StreamWriter log, string label, string? value) => log.WriteLine($"{label,-26} : {value ?? "inconnu"}");

    private static void WriteHeader(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        var b = plan.Backup;
        var t = plan.Target.Profile;
        log.WriteLine(new string('=', 78));
        log.WriteLine($" {AppInfo.Name} — Journal de restauration");
        log.WriteLine(new string('=', 78));
        Line(log, "Date", $"{result.StartedAt:dd.MM.yyyy HH:mm:ss}");
        Line(log, "Version", $"{AppInfo.Name} {AppInfo.Version}");
        log.WriteLine();
        Line(log, "Sauvegarde source", b.Root);
        Line(log, "Format de la sauvegarde", b.IsLegacy ? "ancienne génération (structure + copy-log.txt)" : $"manifest {BackupManifest.FileName} (schéma {b.SchemaVersion})");
        Line(log, "Version de la sauvegarde", b.VersionLabel);
        Line(log, "PC source", b.Source.Computer);
        Line(log, "Utilisateur source", b.Source.User);
        Line(log, "Profil source", b.Source.ProfilePath);
        Line(log, "Sauvegardée le", b.Source.CreatedAt?.ToString("dd.MM.yyyy HH:mm"));
        log.WriteLine();
        Line(log, "PC destination", t.MachineName);
        Line(log, "Session destination", $"{t.UserName} (domaine {t.UserDomain})");
        Line(log, "Profil destination", t.ProfilePath);
        Line(log, "Windows", t.WindowsVersion);
        Line(log, "Droits", t.ElevationLabel);
        log.WriteLine();
        var status = result.Outcome switch
        {
            RestoreOutcome.Completed when result.HasWarnings => "Restauration terminée avec des avertissements",
            RestoreOutcome.Completed => "Restauration terminée",
            RestoreOutcome.Cancelled => "Restauration interrompue par l'utilisateur.",
            _ => "Restauration interrompue : " + (result.FatalMessage ?? "erreur critique"),
        };
        Line(log, "Statut", status);
        Line(log, "Durée", Format.Duration(result.Duration));
        Line(log, "Fichiers existants", ConflictPolicies.Label(plan.Policy) + " (navigateurs : remplacement avec copie de sécurité)");
        if (result.SafetyUsed) Line(log, "Copies de sécurité", plan.SafetyRoot);
    }

    private static void WriteSummary(StreamWriter log, RestorePlan plan, RestoreResult r)
    {
        Title(log, "RÉSUMÉ");
        void L(string label, string value) => log.WriteLine($"{label,-38} : {value}");
        L("Fichiers sélectionnés dans la sauvegarde", $"{Format.Count(r.SelectedFiles)} ({Format.Size(r.Analysis?.Bytes ?? 0)})");
        L("Fichiers à écrire (nouveaux + remplacés)", $"{Format.Count(r.PlannedFiles)} ({Format.Size(r.PlannedBytes)})");
        L("Fichiers restaurés (absents)", Format.Count(r.RestoredFiles));
        L("Fichiers remplacés", Format.Count(r.ReplacedFiles));
        L("Données écrites", Format.Size(r.WrittenBytes));
        L("Fichiers existants conservés", Format.Count(r.KeptFiles));
        L("Fichiers déjà identiques (ignorés)", Format.Count(r.IdenticalFiles));
        L("Éléments ignorés (liens, noms refusés)", Format.Count(r.SkippedCount));
        L("Fichiers en erreur", Format.Count(r.ErrorFiles));
        L("Dossiers en erreur", Format.Count(r.ErrorFolders));
        if (r.InterruptedFiles > 0) L("Fichiers interrompus (annulation)", Format.Count(r.InterruptedFiles));
        if (r.NotProcessedFiles > 0) L("Fichiers non traités (arrêt)", Format.Count(r.NotProcessedFiles));
        L("Contrôle", $"à écrire {Format.Count(r.PlannedFiles)} = écrits {Format.Count(r.WrittenFiles)} + en erreur {Format.Count(r.ErrorFiles)}"
                     + (r.InterruptedFiles > 0 ? $" + interrompus {Format.Count(r.InterruptedFiles)}" : "")
                     + (r.NotProcessedFiles > 0 ? $" + non traités {Format.Count(r.NotProcessedFiles)}" : ""));
        if (plan.Wifi.Count > 0)
            L("Profils Wi-Fi", $"{r.Wifi.Count(w => w.Restored)} restauré(s) / {plan.Wifi.Count}");
        if (r.Outcome == RestoreOutcome.Cancelled)
        {
            log.WriteLine();
            log.WriteLine("Restauration interrompue par l'utilisateur. Les éléments déjà restaurés ont été conservés.");
        }
    }

    private static void WriteBrowserEvents(StreamWriter log, RestorePlan plan)
    {
        if (plan.BrowserEvents.Count == 0) return;
        Title(log, "FERMETURE DES NAVIGATEURS");
        foreach (var line in plan.BrowserEvents) log.WriteLine(line);
    }

    private static void WriteItems(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        Title(log, "ÉLÉMENTS ET CORRESPONDANCES (sauvegarde → destination)");
        foreach (var item in plan.Items)
        {
            var ir = result.Items.FirstOrDefault(i => ReferenceEquals(i.Item, item));
            log.WriteLine();
            log.WriteLine($"[{item.Category}] {item.Label}");
            log.WriteLine($"  Sauvegarde   : {item.RelativeSourcePath}");
            log.WriteLine($"  Destination  : {item.DestinationPath}" + (item.DestinationNote is { Length: > 0 } n ? $"  ({n})" : ""));
            if (ir is null)
            {
                log.WriteLine("  Résultat     : non traité (restauration arrêtée avant cet élément)");
            }
            else
            {
                var parts = new List<string>
                {
                    Format.Plural(ir.Restored, "fichier restauré", "fichiers restaurés"),
                    Format.Plural(ir.Replaced, "remplacé", "remplacés"),
                };
                if (ir.Kept > 0) parts.Add(Format.Plural(ir.Kept, "conservé", "conservés"));
                if (ir.Identical > 0) parts.Add(Format.Plural(ir.Identical, "identique", "identiques"));
                if (ir.Errors > 0) parts.Add(Format.Plural(ir.Errors, "erreur", "erreurs"));
                if (ir.Skipped > 0) parts.Add(Format.Plural(ir.Skipped, "ignoré", "ignorés"));
                if (ir.Interrupted > 0) parts.Add(Format.Plural(ir.Interrupted, "interrompu", "interrompus"));
                log.WriteLine($"  Résultat     : {string.Join(", ", parts)} — {Format.Size(ir.WrittenBytes)} écrits");
                if (ir.Registration != null) log.WriteLine($"  Firefox      : {ir.Registration}");
            }
            if (item.Selection is { } sel)
            {
                var excluded = sel.Rules.Where(r => !r.Value).Select(r => PathUtil.Relative(r.Key, sel.RootPath)).OrderBy(s => s).ToList();
                var included = sel.Rules.Where(r => r.Value).Select(r => PathUtil.Relative(r.Key, sel.RootPath)).OrderBy(s => s).ToList();
                if (excluded.Count > 0)
                {
                    log.WriteLine("  Non restaurés (choix du technicien) :");
                    foreach (var e in excluded) log.WriteLine($"    - {e}");
                }
                if (included.Count > 0)
                {
                    log.WriteLine("  Restaurés dans un dossier exclu :");
                    foreach (var i in included) log.WriteLine($"    + {i}");
                }
            }
            if (item.Excluded.Count > 0) log.WriteLine($"  Jamais restaurés (non portables) : {string.Join(", ", item.Excluded)}");
            if (item.SafetyFolder != null && ir is { Replaced: > 0 }) log.WriteLine($"  Copie de sécurité : {item.SafetyFolder}");
            foreach (var note in item.Notes) log.WriteLine($"  {note}");
        }

        if (plan.NotSelected.Count > 0)
        {
            log.WriteLine();
            log.WriteLine("Non restaurés (choix du technicien / absents) :");
            foreach (var line in plan.NotSelected) log.WriteLine($"  - {line}");
        }
    }

    private static void WriteWifi(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        Title(log, "WI-FI");
        if (plan.Wifi.Count == 0)
        {
            log.WriteLine("Aucun profil Wi-Fi à restaurer.");
            return;
        }
        log.WriteLine($"Profil du même nom déjà présent : {(plan.WifiExisting == WifiExistingPolicy.Replace ? "remplacé" : "conservé")}.");
        foreach (var w in plan.Wifi)
        {
            var r = result.Wifi.FirstOrDefault(x => x.ProfileName == w.Profile.Name);
            log.WriteLine($"{w.Profile.Name} ({w.Profile.Authentication}, {(w.Profile.KeyIncluded ? "clé incluse" : "profil sans clé")}) : {r?.Message ?? "non traité"}");
        }
        log.WriteLine("Les clés Wi-Fi ne sont jamais écrites dans ce journal.");
    }

    private static void WriteWallpaper(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        Title(log, "FOND D'ÉCRAN");
        if (plan.Wallpaper is null)
        {
            log.WriteLine("Non restauré (non sélectionné ou absent de la sauvegarde).");
            return;
        }
        log.WriteLine($"Destination : {plan.Wallpaper.TranscodedPath}");
        if (result.Wallpaper is { } w)
        {
            log.WriteLine($"Résultat    : {w.Message}");
            if (w.StoredPath != null) log.WriteLine($"Copie       : {w.StoredPath}");
            if (w.PreviousWallpaper is { Length: > 0 } p) log.WriteLine($"Précédent   : {p}");
        }
        else log.WriteLine("Résultat    : non traité");
    }

    private static void WritePrinters(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        Title(log, "IMPRIMANTES");
        if (plan.PrintersToRestore.Count == 0)
        {
            log.WriteLine(plan.Backup.Printers is null ? "Aucune imprimante dans la sauvegarde." : "Aucune imprimante sélectionnée.");
            return;
        }
        log.WriteLine("Sélection : " + string.Join(", ", plan.PrintersToRestore.Select(p => p.TargetName)));
        log.WriteLine(plan.PrintersNeedAdmin
            ? "Droits administrateur demandés une seule fois (pilote, port, file) ; les opérations du profil restent sous la session."
            : "Aucune opération administrateur nécessaire.");
        if (result.Printers is not { } r)
        {
            log.WriteLine("Non traité (restauration arrêtée avant cette étape).");
            return;
        }
        if (r.Error != null) log.WriteLine("ERREUR : " + r.Error);
        if (r.ReportText is { Length: > 0 } text)
            foreach (var line in text.Replace("\r\n", "\n").Split('\n')) log.WriteLine(line);
        if (r.JournalPath != null) log.WriteLine($"Journal des modifications d'imprimantes : {r.JournalPath}");
        log.WriteLine("Aucune page de test n'est imprimée par DataBridge.");
    }

    private static void WriteOutlook(StreamWriter log, RestorePlan plan, RestoreResult result)
    {
        Title(log, "OUTLOOK — BOÎTE AUX LETTRES");
        if (plan.Outlook is null)
        {
            log.WriteLine(plan.Backup.Mailbox is null ? "Aucune boîte Outlook dans la sauvegarde." : "Non restaurée (non sélectionnée).");
            return;
        }
        log.WriteLine($"Sauvegarde       : {plan.Backup.Mailbox?.MailboxName}");
        log.WriteLine($"Mode             : {(plan.Outlook.IntoNewFolder ? "dossier « Restauration <date> »" : "fusion dans les dossiers existants")}, doublons ignorés");
        if (result.Outlook is not { } r)
        {
            log.WriteLine("Non traité (restauration arrêtée avant cette étape).");
            return;
        }
        foreach (var line in r.LogLines()) log.WriteLine(line);
        log.WriteLine("Ajout uniquement : rien n'est supprimé ni modifié dans la boîte de destination, aucun mail n'est envoyé.");
    }

    private void WriteSection(StreamWriter log, string title, string section, int count)
    {
        Title(log, count >= 0 ? $"{title} ({Format.Count(count)})" : title);
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
