using System.Collections.Concurrent;
using DataBridge.Core.Detection;

namespace DataBridge.Core.Copy;

public enum IssueKind
{
    Locked,
    AccessDenied,
    NotFound,
    PathTooLong,
    CloudOnly,
    ReadError,
    WriteError,
    Link,
    CancelledFile,
    CopiedWhileOpen,
    Other,
    // Restore (2.0)
    Replaced,
    KeptExisting,
    UnsafePath,
    NotRestored,
    Restored,
}

public enum IssueSeverity { Info, Ignored, Error }

public sealed record CopyIssue(DateTime Time, IssueKind Kind, IssueSeverity Severity, string Path, string Message)
{
    public string KindLabel => Kind switch
    {
        IssueKind.Locked => "Fichier verrouillé",
        IssueKind.AccessDenied => "Accès refusé",
        IssueKind.NotFound => "Fichier disparu",
        IssueKind.PathTooLong => "Chemin trop long",
        IssueKind.CloudOnly => "Disponible uniquement en ligne",
        IssueKind.ReadError => "Erreur de lecture",
        IssueKind.WriteError => "Erreur d'écriture",
        IssueKind.Link => "Lien / jonction non suivi",
        IssueKind.CancelledFile => "Copie annulée",
        IssueKind.CopiedWhileOpen => "Copié alors qu'ouvert",
        IssueKind.Replaced => "Fichier remplacé",
        IssueKind.KeptExisting => "Fichier existant conservé",
        IssueKind.UnsafePath => "Chemin refusé (sécurité)",
        IssueKind.NotRestored => "Non restauré",
        IssueKind.Restored => "Restauré",
        _ => "Erreur",
    };
}

/// <summary>
/// Live progress shared between the copy thread (writer) and the UI timer (reader).
/// Plain fields: 64-bit reads/writes are atomic, and the UI only displays them.
/// Denominators (<see cref="PlannedFiles"/>, <see cref="PlannedBytes"/>) are the files really planned for the copy:
/// cloud-only files left on "do not download" are never part of them.
/// </summary>
public sealed class CopyProgress
{
    private readonly ConcurrentQueue<CopyIssue> _recent = new();

    public volatile string Phase = "Préparation";
    public volatile bool Counting = true;
    public volatile string Category = "";
    public volatile string Item = "";
    public volatile string CurrentFile = "";
    public long CountedFiles;
    public long SelectedFiles;
    public long PlannedFiles;
    public long PlannedBytes;
    public long PlannedCloudSkipped;
    public long CopiedFiles;
    public long CopiedBytes;
    /// <summary>Bytes of planned files that ended in error or were interrupted: processed, but not copied.</summary>
    public long FailedBytes;
    public long CurrentFileBytes;
    public int ErrorFiles;
    public int ErrorFolders;
    public int Locked;
    public int SkippedFiles;
    public int InterruptedFiles;
    public int CloudSkipped;
    public long CloudSkippedBytes;
    public int Duplicates;
    public int WifiDone;
    public int WifiTotal;
    /// <summary>Printers / Outlook phases (2.1), after the files: items processed out of the total announced by the engine.</summary>
    public volatile bool ExtraPhase;
    public int ExtraDone;
    public int ExtraTotal;

    public int Errors => ErrorFiles + ErrorFolders;
    public long ProcessedFiles => Interlocked.Read(ref CopiedFiles) + ErrorFiles + InterruptedFiles;
    public long CopiedBytesWithCurrent => Interlocked.Read(ref CopiedBytes) + Interlocked.Read(ref CurrentFileBytes);
    public long ProcessedBytesWithCurrent => CopiedBytesWithCurrent + Interlocked.Read(ref FailedBytes);

    public void AddRecent(CopyIssue issue)
    {
        _recent.Enqueue(issue);
        while (_recent.Count > 200 && _recent.TryDequeue(out _)) { }
    }

    public IReadOnlyList<CopyIssue> RecentIssues() => _recent.Reverse().ToList();
}

public enum CopyOutcome { Completed, Cancelled, Failed }

public sealed class JobResult(CopyJob job, string destinationFull)
{
    public CopyJob Job { get; } = job;
    public string DestinationFull { get; } = destinationFull;
    public long CopiedFiles;
    public long CopiedBytes;
    public int ErrorFiles;
    public int ErrorFolders;
    public int Skipped;
    public int Interrupted;
    public int CloudSkipped;
    public int Duplicates;
    public bool Started;
}

/// <summary>
/// Outcome of a copy. Counters are kept apart on purpose:
/// selected (everything checked) / planned (really read and copied with the current options) / copied (finished
/// successfully) / cloud-only skipped / other skipped / errors. A cloud-only file that is not downloaded is
/// selected and skipped, never planned nor copied. When nothing changes on disk during the copy:
/// PlannedCopyFileCount = CopiedFileCount + ErrorFileCount + InterruptedFileCount + NotProcessedFileCount.
/// </summary>
public sealed class CopyResult
{
    public CopyOutcome Outcome { get; set; }
    public string BackupPath { get; init; } = "";
    public string? LogPath { get; set; }
    /// <summary>databridge-manifest.json written at the root of the backup (2.0), or the reason it could not be written.</summary>
    public string? ManifestPath { get; set; }
    public string? ManifestError { get; set; }

    /// <summary>Exact count made when the copy started (see <see cref="PlanCalculator.CountNow"/>).</summary>
    public CopyForecast Forecast { get; set; }
    public bool SkipCloudOnly { get; init; }
    public long SelectedFileCount => Forecast.SelectedFileCount;
    public long SelectedBytes => Forecast.SelectedBytes;
    public long PlannedCopyFileCount => Forecast.PlannedCopyFileCount;
    public long PlannedCopyBytes => Forecast.PlannedCopyBytes;

    public long CopiedFileCount { get; set; }
    public long CopiedBytes { get; set; }
    public int SkippedCloudOnlyCount { get; set; }
    public long SkippedCloudOnlyBytes { get; set; }
    /// <summary>Other items deliberately not copied (symbolic links / junctions, never followed).</summary>
    public int SkippedFileCount { get; set; }
    public int ErrorFileCount { get; set; }
    public int ErrorFolderCount { get; set; }
    /// <summary>Large file aborted by the user's cancellation (partial copy removed).</summary>
    public int InterruptedFileCount { get; set; }
    public int LockedFileCount { get; set; }
    public int DuplicateCount { get; set; }
    public int CopiedWhileOpenCount { get; set; }

    public int ErrorCount => ErrorFileCount + ErrorFolderCount;
    public long ProcessedFileCount => CopiedFileCount + ErrorFileCount + InterruptedFileCount;
    /// <summary>Planned files never reached (copy cancelled or stopped by a critical error).</summary>
    public long NotProcessedFileCount => Outcome == CopyOutcome.Completed ? 0 : Math.Max(0, PlannedCopyFileCount - ProcessedFileCount);
    /// <summary>Difference due to files created or deleted on the source during the copy (normally 0).</summary>
    public long Drift => Outcome == CopyOutcome.Completed ? ProcessedFileCount - PlannedCopyFileCount : 0;
    public double ProcessedPercent => PlannedCopyFileCount > 0 ? Math.Min(100, ProcessedFileCount * 100.0 / PlannedCopyFileCount) : 100;

    public TimeSpan Duration { get; set; }
    public string? FatalMessage { get; set; }
    public DateTime StartedAt { get; init; }
    public List<JobResult> Jobs { get; } = [];
    public List<WifiExportResult> Wifi { get; } = [];
    /// <summary>Printers exported (2.1), null when none was selected.</summary>
    public PrinterTransfer.PrinterBackupResult? Printers { get; set; }
    /// <summary>Outlook mailbox exported to a PST (2.1), null when none was chosen.</summary>
    public Outlook.OutlookTransferResult? Outlook { get; set; }
    public Dictionary<IssueKind, int> IssueCounts { get; } = [];

    public int Count(IssueKind kind) => IssueCounts.TryGetValue(kind, out var n) ? n : 0;
    /// <summary>Printers and mailbox exported without problem (true when they were not requested).</summary>
    public bool ExtrasOk => (Printers?.Ok ?? true) && (Outlook?.Ok ?? true);

    public int WifiExported => Wifi.Count(w => w.Exported);
    public int WifiKeysIncluded => Wifi.Count(w => w.KeyIncluded);
    public int WifiKeysViaAdmin => Wifi.Count(w => w.KeyIncluded && w.KeyViaAdmin);
    public int WifiKeysProtected => Wifi.Count(w => w.Exported && w.KeyStatus is WifiKeyStatus.NotAccessible or WifiKeyStatus.Unknown);
    public int WifiWithoutKey => Wifi.Count(w => w.Exported && w.KeyStatus == WifiKeyStatus.NoKey);
}
