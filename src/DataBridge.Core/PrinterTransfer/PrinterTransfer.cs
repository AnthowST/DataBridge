using DataBridge.Core.Outlook;
using DataBridge.Printers.Archive;
using DataBridge.Printers.Elevation;
using DataBridge.Printers.Export;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Model;
using DataBridge.Printers.Reporting;
using DataBridge.Printers.Util;
using PrintersRestoreEngine = DataBridge.Printers.Restore.RestoreEngine;
using PrintersRestorePlan = DataBridge.Printers.Restore.RestorePlan;
using PrintersRestoreProgress = DataBridge.Printers.Restore.RestoreProgress;

namespace DataBridge.Core.PrinterTransfer;

/// <summary>Printers selected for the backup (export is read-only: no administrator rights).</summary>
public sealed record PrinterBackupJob(LiveInventory Inventory, IReadOnlyList<string> PrinterIds)
{
    /// <summary>Same .prnbak format as PrintBridge: the archive can also be opened with PrintBridge.</summary>
    public const string DestinationRelative = @"Imprimantes\Imprimantes.prnbak";

    public IReadOnlyList<LivePrinter> Printers => Inventory.Printers.Where(p => PrinterIds.Contains(p.Entry.Id)).ToList();
}

public sealed class PrinterBackupResult
{
    public string? ArchivePath { get; set; }
    public long ArchiveSize { get; set; }
    /// <summary>Printers really written to the archive, with the state of each component.</summary>
    public List<PrinterEntry> Printers { get; } = [];
    public List<string> Notes { get; } = [];
    public string? Error { get; set; }
    public bool Cancelled { get; set; }
    public bool Ok => Error is null && !Cancelled;
    public int Supported => Printers.Count(p => p.Support == SupportLevel.Supported);

    public IEnumerable<string> LogLines()
    {
        if (ArchivePath != null) yield return $"Archive : {ArchivePath} ({Format.Size(ArchiveSize)}) — relue et vérifiée (empreintes SHA-256).";
        if (Error != null) yield return "ÉCHEC : " + Error;
        if (Cancelled) yield return "Interrompu par l'utilisateur.";
        foreach (var p in Printers)
        {
            yield return $"{p.Name} — {Labels.Kind(p.Kind)} — {Labels.Support(p.Support)}";
            foreach (var c in p.Components) yield return $"    {Labels.State(c.State),-12} {c.Component} : {c.Detail}";
            foreach (var w in p.Warnings) yield return $"    ! {w}";
        }
        foreach (var n in Notes) yield return n;
    }
}

/// <summary>Printers of a backup opened for a restore: the archive is checked, then this PC is analysed (nothing is changed).</summary>
public sealed class PrinterRestoreSource : IDisposable
{
    public required string ArchivePath { get; init; }
    public OpenedArchive? Archive { get; init; }
    public PrintersRestorePlan? Plan { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<ArchiveCheck> Checks => Archive?.Checks ?? [];
    public bool Usable => Plan != null;

    public static PrinterRestoreSource Open(string archivePath)
    {
        OpenedArchive? archive = null;
        try
        {
            archive = ArchiveReader.Open(archivePath);
            if (!archive.IsUsable)
                return new PrinterRestoreSource
                {
                    ArchivePath = archivePath,
                    Archive = archive,
                    Error = "Sauvegarde des imprimantes inutilisable : " + string.Join(" ; ",
                        archive.Checks.Where(c => c.Level == CheckLevel.Error).Select(c => $"{c.Label} : {c.Detail}")),
                };
            var plan = DataBridge.Printers.Restore.RestoreAnalyzer.Analyze(archive);
            return new PrinterRestoreSource { ArchivePath = archivePath, Archive = archive, Plan = plan };
        }
        catch (Exception ex)
        {
            return new PrinterRestoreSource { ArchivePath = archivePath, Archive = archive, Error = "Lecture de la sauvegarde des imprimantes impossible : " + ex.Message };
        }
    }

    public void Dispose() => Archive?.Dispose();
}

public sealed class PrinterRestoreResult
{
    public RestoreReport? Report { get; set; }
    public string? Error { get; set; }
    public string? ReportText { get; set; }
    public string? JournalPath { get; set; }
    public int Restored => Report?.Printers.Count(p => p.Outcome == PrinterOutcome.Restored) ?? 0;
    public int WithDifferences => Report?.Printers.Count(p => p.Outcome is PrinterOutcome.RestoredWithDifferences or PrinterOutcome.Partial) ?? 0;
    public int Kept => Report?.Printers.Count(p => p.Outcome == PrinterOutcome.KeptExisting) ?? 0;
    public int Failed => Report?.Printers.Count(p => p.Outcome == PrinterOutcome.Failed) ?? 0;
    public bool Ok => Error is null && Failed == 0 && Report?.General.All(g => g.Status != StepStatus.Error) != false;
}

/// <summary>
/// Printers for DataBridge, with the PrintBridge engine. The export only reads (session rights). The restore keeps every
/// per-user operation (connections, preferences, default printer) in this process; machine operations (driver, TCP/IP
/// port, queue) go through ONE elevated instance of DataBridge.exe ("--printer-worker", UAC prompt) that only accepts a
/// closed list of operations, and only when a selected printer needs it. Never a test page or any print job.
/// </summary>
public static class PrinterTransferService
{
    public static PrinterBackupResult Backup(PrinterBackupJob job, string backupRoot, Action<string> status, CancellationToken ct)
    {
        var result = new PrinterBackupResult();
        var path = Path.Combine(backupRoot, PrinterBackupJob.DestinationRelative);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var export = Exporter.Export(job.Inventory, job.PrinterIds.ToList(), path, new ExportOptions(),
                new Reporter<ExportProgress>(p => status(string.IsNullOrEmpty(p.Printer) ? p.Step : $"{p.Printer} — {p.Step}")),
                new OperationLog(), ct);
            result.ArchivePath = export.ArchivePath;
            result.ArchiveSize = export.ArchiveSize;
            result.Printers.AddRange(export.Inventory.Printers);
            result.Notes.AddRange(export.Manifest.Notes);
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    public static PrinterRestoreResult Restore(PrintersRestorePlan plan, Action<string> status, CancellationToken ct)
    {
        var result = new PrinterRestoreResult();
        var engine = new PrintersRestoreEngine(plan, () => SystemInfo.IsElevated() && plan.RunsAsSessionUser
            ? new MachineOps()
            : ElevatedMachineOps.Start(Environment.ProcessPath!, TimeSpan.FromMinutes(3)));
        try
        {
            result.JournalPath = engine.Journal.Path;
            var report = engine.Run(new Reporter<PrintersRestoreProgress>(p =>
                status(string.IsNullOrEmpty(p.Printer) ? p.Step : $"{p.Printer} — {p.Step}")), ct);
            result.Report = report;
            // Reachability of the restored printers (TCP connection / share access only: nothing is printed).
            status("Vérification de la joignabilité des imprimantes…");
            Task.WhenAll(report.Printers.Where(p => p.ConfigurationRestored || p.Outcome == PrinterOutcome.KeptExisting)
                .Select(PrintersRestoreEngine.ProbeAsync)).Wait(TimeSpan.FromSeconds(60));
            result.ReportText = ReportWriter.RestoreText(report);
        }
        catch (OperationCanceledException)
        {
            result.Error = "Interrompu par l'utilisateur.";
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        finally
        {
            try { engine.Machine?.Dispose(); } catch { /* worker already gone */ }
        }
        return result;
    }
}
