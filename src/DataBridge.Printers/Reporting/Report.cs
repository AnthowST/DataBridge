using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using DataBridge.Printers.Export;
using DataBridge.Printers.Model;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Reporting;

public enum StepStatus { Ok, Info, Warning, Error, Skipped }

public sealed record StepResult(StepStatus Status, string Title, string? Detail = null);

public enum CheckStatus { Identical, Different, Missing, NotChecked }

public sealed record CheckItem(string Category, string Label, string? Expected, string? Actual, CheckStatus Status)
{
    public string StatusText => ReportWriter.CheckLabel(Status);
}

public enum PrinterOutcome { Restored, RestoredWithDifferences, Partial, KeptExisting, Skipped, Failed }

/// <summary>Per-printer result. The five levels requested are kept separate: configuration, reachability, share access, test page, physical confirmation.</summary>
public sealed class PrinterReport : INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string Kind { get; set; } = "";
    public bool IsConnection { get; set; }
    public string? PortHost { get; set; }
    public int PortNumber { get; set; }

    private PrinterOutcome _outcome;
    public PrinterOutcome Outcome { get => _outcome; set { Set(ref _outcome, value); Notify(nameof(OutcomeText)); } }
    public List<StepResult> Steps { get; set; } = new();
    public List<CheckItem> Checks { get; set; } = new();

    private string? _reachability;
    public string? Reachability { get => _reachability; set => Set(ref _reachability, value); }
    private bool? _reachable;
    public bool? Reachable { get => _reachable; set { Set(ref _reachable, value); Notify(nameof(ReachableText)); } }
    private string? _shareAccess;
    public string? ShareAccess { get => _shareAccess; set { Set(ref _shareAccess, value); Notify(nameof(ShareText)); } }
    private bool? _shareOk;
    public bool? ShareOk { get => _shareOk; set { Set(ref _shareOk, value); Notify(nameof(ShareText)); } }
    private bool _testPageSent;
    public bool TestPageSent { get => _testPageSent; set { Set(ref _testPageSent, value); Notify(nameof(TestPageText)); } }
    private string? _testPageDetail;
    public string? TestPageDetail { get => _testPageDetail; set { Set(ref _testPageDetail, value); Notify(nameof(TestPageText)); } }
    private bool _physicalConfirmed;
    public bool PhysicalConfirmed { get => _physicalConfirmed; set => Set(ref _physicalConfirmed, value); }

    public bool ConfigurationRestored => Outcome is PrinterOutcome.Restored or PrinterOutcome.RestoredWithDifferences or PrinterOutcome.Partial;

    public string OutcomeText => Outcome switch
    {
        PrinterOutcome.Restored => "Restaurée et vérifiée",
        PrinterOutcome.RestoredWithDifferences => "Restaurée, avec différences",
        PrinterOutcome.Partial => "Restauration partielle",
        PrinterOutcome.KeptExisting => "Existante conservée",
        PrinterOutcome.Skipped => "Ignorée",
        _ => "Échec",
    };

    public string ReachableText => Reachable switch { true => "Joignable", false => "Injoignable", _ => "Non testé" };
    public string ShareText => !IsConnection ? "Sans objet" : ShareOk switch { true => "Accès OK", false => "Refusé / impossible", _ => "Non testé" };
    public string TestPageText => TestPageSent ? "Envoyée au spouleur" : TestPageDetail ?? "Non envoyée";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Notify(name!);
    }
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RestoreReport
{
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public string ArchivePath { get; set; } = "";
    public SourceInfo? Source { get; set; }
    public SourceInfo Target { get; set; } = new();
    public string? ElevatedAccount { get; set; }
    public List<StepResult> General { get; set; } = new();
    public List<PrinterReport> Printers { get; set; } = new();
    public string? JournalPath { get; set; }
    public string? LogPath { get; set; }
}

public static class ReportWriter
{
    public static string RestoreJson(RestoreReport r) => Json.Serialize(r);

    public static string RestoreHtml(RestoreReport r)
    {
        var sb = Begin("Rapport de restauration des imprimantes (DataBridge)");
        sb.Append($"<h1>Rapport de restauration</h1><p>Archive : <code>{E(r.ArchivePath)}</code><br>Début : {r.Started:dd/MM/yyyy HH:mm:ss} — fin : {r.Finished:dd/MM/yyyy HH:mm:ss}<br>");
        if (r.Source != null) sb.Append($"Source : {E(r.Source.ComputerName)} ({E(r.Source.OsProductName)} build {r.Source.OsBuild}, {E(r.Source.UserName)})<br>");
        sb.Append($"Cible : {E(r.Target.ComputerName)} ({E(r.Target.OsProductName)} build {r.Target.OsBuild}, {E(r.Target.UserName)})");
        if (r.ElevatedAccount != null) sb.Append($"<br>Opérations administrateur exécutées sous : {E(r.ElevatedAccount)}");
        sb.Append("</p>");
        if (r.General.Count > 0)
        {
            sb.Append("<h2>Général</h2><ul>");
            foreach (var s in r.General) sb.Append($"<li class='{s.Status}'>{E(s.Title)}{(s.Detail != null ? " — " + E(s.Detail) : "")}</li>");
            sb.Append("</ul>");
        }
        sb.Append("<h2>Synthèse</h2><table><tr><th>Imprimante</th><th>Configuration</th><th>Joignable</th><th>Partage</th><th>Page de test</th><th>Impression confirmée</th></tr>");
        foreach (var p in r.Printers)
            sb.Append($"<tr><td>{E(p.TargetName)}</td><td>{E(p.OutcomeText)}</td><td>{E(p.ReachableText)}</td><td>{E(p.ShareText)}</td><td>{E(p.TestPageText)}</td><td>{(p.PhysicalConfirmed ? "Oui (confirmée par l'utilisateur)" : "Non confirmée")}</td></tr>");
        sb.Append("</table>");
        foreach (var p in r.Printers)
        {
            sb.Append($"<h2>{E(p.TargetName)}</h2><p>Source : « {E(p.SourceName)} » — {E(p.Kind)} — <b>{E(p.OutcomeText)}</b></p><ul>");
            foreach (var s in p.Steps) sb.Append($"<li class='{s.Status}'>{E(StatusLabel(s.Status))} — {E(s.Title)}{(s.Detail != null ? " : " + E(s.Detail) : "")}</li>");
            sb.Append("</ul>");
            if (p.Reachability != null) sb.Append($"<p>Connectivité : {E(p.Reachability)}</p>");
            if (p.ShareAccess != null) sb.Append($"<p>Partage : {E(p.ShareAccess)}</p>");
            if (p.Checks.Count > 0)
            {
                sb.Append("<table><tr><th>Catégorie</th><th>Élément</th><th>Source</th><th>Cible</th><th>Résultat</th></tr>");
                foreach (var c in p.Checks)
                    sb.Append($"<tr class='{c.Status}'><td>{E(c.Category)}</td><td>{E(c.Label)}</td><td>{E(c.Expected)}</td><td>{E(c.Actual)}</td><td>{E(CheckLabel(c.Status))}</td></tr>");
                sb.Append("</table>");
            }
        }
        sb.Append($"<p class='note'>Journal des modifications : <code>{E(r.JournalPath)}</code><br>Une absence d'erreur ne prouve pas l'impression : seules les vérifications ci-dessus et votre confirmation de la page de test le font.</p>");
        return End(sb);
    }

    public static string ExportHtml(ExportResult r)
    {
        var m = r.Manifest;
        var sb = Begin("Rapport d'export des imprimantes (DataBridge)");
        sb.Append($"<h1>Rapport d'export</h1><p>Archive : <code>{E(r.ArchivePath)}</code> ({r.ArchiveSize / 1024} Ko)<br>Date : {m.ExportedAt:dd/MM/yyyy HH:mm:ss}<br>");
        sb.Append($"Source : {E(m.Source.ComputerName)} — {E(m.Source.OsProductName)} {E(m.Source.OsDisplayVersion)} build {m.Source.OsBuild}.{m.Source.OsUbr} — {E(m.Source.Architecture)} — utilisateur {E(m.Source.UserName)}</p>");
        foreach (var p in r.Inventory.Printers)
        {
            sb.Append($"<h2>{E(p.Name)}</h2><p>{E(Labels.Kind(p.Kind))} — {E(Labels.Support(p.Support))}</p>");
            sb.Append("<table><tr><th>Composant</th><th>État</th><th>Détail</th></tr>");
            foreach (var c in p.Components) sb.Append($"<tr class='{c.State}'><td>{E(c.Component)}</td><td>{E(Labels.State(c.State))}</td><td>{E(c.Detail)}</td></tr>");
            sb.Append("</table>");
            if (p.Warnings.Count > 0)
            {
                sb.Append("<ul>");
                foreach (var w in p.Warnings) sb.Append($"<li class='Warning'>{E(w)}</li>");
                sb.Append("</ul>");
            }
        }
        sb.Append("<h2>Pilotes</h2><table><tr><th>Pilote</th><th>Version</th><th>Package</th><th>Origine</th><th>État</th></tr>");
        foreach (var d in r.Inventory.Drivers)
            sb.Append($"<tr><td>{E(d.Name)}</td><td>{E(d.Version)}</td><td>{E(d.InfOriginalName)} {E(d.PublishedInf)}</td><td>{(d.IsInbox ? "Intégré à Windows" : "Tiers" + (d.Signer != null ? " — signé par " + E(d.Signer) : ""))}</td><td>{(d.IsInbox ? "Non exporté (intégré)" : d.Exported ? "Exporté" : "Non exporté : " + E(d.NotExportedReason))}</td></tr>");
        sb.Append("</table><h2>Limites</h2><ul>");
        foreach (var n in m.Notes) sb.Append($"<li>{E(n)}</li>");
        sb.Append("<li>Non couverts : logiciels de numérisation, utilitaires constructeur, applications Microsoft Store associées, réglages enregistrés par le constructeur hors des mécanismes d'impression de Windows.</li></ul>");
        sb.Append("<h2>Journal</h2><pre>" + E(r.Log.Text) + "</pre>");
        return End(sb);
    }

    /// <summary>Plain-text report (shown in the report box of the restore tab).</summary>
    public static string RestoreText(RestoreReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Restauration du {r.Started:dd.MM.yyyy HH:mm} — archive : {r.ArchivePath}");
        if (r.Source != null) sb.AppendLine($"Source : {r.Source.ComputerName} ({r.Source.OsProductName}, build {r.Source.OsBuild})   Cible : {r.Target.ComputerName}");
        foreach (var g in r.General) sb.AppendLine($"{StatusLabel(g.Status),-9} {g.Title}{(g.Detail != null ? " : " + g.Detail : "")}");
        foreach (var p in r.Printers)
        {
            sb.AppendLine();
            sb.AppendLine($"=== {p.TargetName}{(p.TargetName != p.SourceName ? $" (source : {p.SourceName})" : "")} — {p.OutcomeText}");
            sb.AppendLine($"    Joignable : {p.ReachableText}   Partage : {p.ShareText}   Page de test : {p.TestPageText}   Impression confirmée : {(p.PhysicalConfirmed ? "oui" : "non")}");
            if (p.Reachability != null) sb.AppendLine($"    {p.Reachability}");
            if (p.ShareAccess != null) sb.AppendLine($"    {p.ShareAccess}");
            foreach (var s in p.Steps) sb.AppendLine($"  {StatusLabel(s.Status),-9} {s.Title}{(s.Detail != null ? " : " + s.Detail : "")}");
            if (p.Checks.Count > 0)
            {
                sb.AppendLine("  Vérification (relu sur ce PC) :");
                foreach (var c in p.Checks)
                    sb.AppendLine($"    {CheckLabel(c.Status),-11} {c.Category} / {c.Label} : {c.Expected} → {c.Actual}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"Journal : {r.JournalPath}");
        sb.AppendLine("Une absence d'erreur ne prouve pas l'impression : seule votre confirmation après la page de test le fait.");
        return sb.ToString();
    }

    public static string StatusLabel(StepStatus s) => s switch
    {
        StepStatus.Ok => "OK",
        StepStatus.Info => "Info",
        StepStatus.Warning => "Attention",
        StepStatus.Error => "Erreur",
        _ => "Ignoré",
    };

    public static string CheckLabel(CheckStatus s) => s switch
    {
        CheckStatus.Identical => "Identique",
        CheckStatus.Different => "Différent",
        CheckStatus.Missing => "Absent",
        _ => "Non vérifié",
    };

    private static StringBuilder Begin(string title)
    {
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang='fr'><head><meta charset='utf-8'><title>{E(title)}</title><style>");
        sb.Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1f2328}h1{font-size:22px}h2{font-size:17px;margin-top:28px;border-bottom:1px solid #ddd}");
        sb.Append("table{border-collapse:collapse;margin:8px 0;width:100%}th,td{border:1px solid #ddd;padding:4px 8px;text-align:left;font-size:13px;vertical-align:top}th{background:#f3f4f6}");
        sb.Append(".Error,.Failed,.Different,.Missing{color:#b42318}.Warning,.Partial{color:#9a6700}.Ok,.Identical,.Exported{color:#1a7f37}code{font-size:12px}pre{font-size:11px;white-space:pre-wrap}.note{color:#555;font-size:13px}");
        sb.Append("</style></head><body>");
        return sb;
    }

    private static string End(StringBuilder sb) => sb.Append("</body></html>").ToString();
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
}
