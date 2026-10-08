using DataBridge.Core;
using DataBridge.Core.Outlook;
using DataBridge.Core.PrinterTransfer;

namespace DataBridge.App.ViewModels;

/// <summary>Result lines of the Outlook mailbox and printers steps (2.1), for the backup and restore result screens.</summary>
public static class ExtraResultLines
{
    public static List<ResultLine> Outlook(OutlookTransferResult? r, bool restore)
    {
        if (r is null) return [new(MarkKind.Off, restore ? "Boîte Outlook non restaurée" : "Aucune boîte Outlook sauvegardée")];
        if (r.Error != null) return [new(MarkKind.Error, "Boîte Outlook : " + r.Error)];
        var lines = new List<ResultLine>();
        if (r.Cancelled) lines.Add(new(MarkKind.Warn, "Boîte Outlook interrompue par l'annulation"));
        var verb = restore ? "restauré" : "sauvegardé";
        if (r.Categories.HasFlag(OutlookCategories.Mails)) lines.Add(new(MarkKind.Ok, Format.Plural(r.Mails, "mail " + verb, "mails " + verb + "s")));
        if (r.Categories.HasFlag(OutlookCategories.Calendar)) lines.Add(new(MarkKind.Ok, Format.Plural(r.Events, "événement " + verb, "événements " + verb + "s")));
        if (r.Categories.HasFlag(OutlookCategories.Contacts)) lines.Add(new(MarkKind.Ok, Format.Plural(r.Contacts, "contact " + verb, "contacts " + verb + "s")));
        if (r.Categories.HasFlag(OutlookCategories.Autocomplete))
            lines.Add(r.AutocompleteOk ? new(MarkKind.Ok, "Autocomplétion " + (restore ? "restaurée" : "sauvegardée")) : new(MarkKind.Warn, "Autocomplétion non traitée (voir le journal)"));
        if (restore && r.Duplicates > 0) lines.Add(new(MarkKind.Ok, Format.Plural(r.Duplicates, "élément déjà présent non recréé", "éléments déjà présents non recréés")));
        if (r.Errors > 0) lines.Add(new(MarkKind.Error, Format.Plural(r.Errors, "élément Outlook en erreur", "éléments Outlook en erreur")));
        if (r.Detail is { NotProcessed: > 0 } d) lines.Add(new(MarkKind.Warn, Format.Plural(d.NotProcessed, "élément Outlook non traité", "éléments Outlook non traités")));
        return lines;
    }

    public static List<ResultLine> PrintersBackup(PrinterBackupResult? r)
    {
        if (r is null) return [new(MarkKind.Off, "Aucune imprimante sauvegardée")];
        if (r.Error != null) return [new(MarkKind.Error, "Imprimantes : " + r.Error)];
        if (r.Cancelled) return [new(MarkKind.Warn, "Imprimantes interrompues par l'annulation")];
        var lines = new List<ResultLine> { new(MarkKind.Ok, Format.Plural(r.Printers.Count, "imprimante sauvegardée", "imprimantes sauvegardées") + $" ({Format.Size(r.ArchiveSize)})") };
        var detectedOnly = r.Printers.Count - r.Supported;
        if (detectedOnly > 0) lines.Add(new(MarkKind.Warn, Format.Plural(detectedOnly, "imprimante pour information seulement", "imprimantes pour information seulement")));
        return lines;
    }

    public static List<ResultLine> PrintersRestore(PrinterRestoreResult? r)
    {
        if (r is null) return [new(MarkKind.Off, "Aucune imprimante restaurée")];
        var lines = new List<ResultLine>();
        if (r.Error != null) lines.Add(new(MarkKind.Error, "Imprimantes : " + r.Error));
        if (r.Report is { } report)
        {
            foreach (var g in report.General.Where(g => g.Status == DataBridge.Printers.Reporting.StepStatus.Error))
                lines.Add(new(MarkKind.Error, g.Title + (g.Detail is { Length: > 0 } d ? " : " + d : "")));
            foreach (var p in report.Printers)
            {
                var kind = p.Outcome switch
                {
                    DataBridge.Printers.Reporting.PrinterOutcome.Restored or DataBridge.Printers.Reporting.PrinterOutcome.KeptExisting => MarkKind.Ok,
                    DataBridge.Printers.Reporting.PrinterOutcome.Failed => MarkKind.Error,
                    _ => MarkKind.Warn,
                };
                lines.Add(new(kind, $"{p.TargetName} : {p.OutcomeText}"));
            }
        }
        if (lines.Count == 0) lines.Add(new(MarkKind.Off, "Aucune imprimante restaurée"));
        return lines;
    }
}
