using System.Collections.Concurrent;
using DataBridge.Core.Copy;

namespace DataBridge.Core.Restore;

/// <summary>
/// Live progress shared between the restore thread (writer) and the UI timer (reader). Denominators are the files that
/// will really be written (restored + replaced); kept and identical files are counted apart.
/// </summary>
public sealed class RestoreProgress
{
    private readonly ConcurrentQueue<CopyIssue> _recent = new();

    public volatile string Phase = "Préparation";
    public volatile bool Counting = true;
    public volatile string Category = "";
    public volatile string Item = "";
    public volatile string CurrentFile = "";
    public long CountedFiles;
    public long PlannedFiles;
    public long PlannedBytes;
    public long RestoredFiles;
    public long ReplacedFiles;
    public long WrittenBytes;
    public long FailedBytes;
    public long CurrentFileBytes;
    public int KeptFiles;
    public int IdenticalFiles;
    public int ErrorFiles;
    public int ErrorFolders;
    public int Skipped;
    public int InterruptedFiles;
    public int WifiDone;
    public int WifiTotal;

    public long WrittenFiles => Interlocked.Read(ref RestoredFiles) + Interlocked.Read(ref ReplacedFiles);
    public int Errors => ErrorFiles + ErrorFolders;
    public long ProcessedFiles => WrittenFiles + ErrorFiles + InterruptedFiles;
    public long WrittenBytesWithCurrent => Interlocked.Read(ref WrittenBytes) + Interlocked.Read(ref CurrentFileBytes);
    public long ProcessedBytesWithCurrent => WrittenBytesWithCurrent + Interlocked.Read(ref FailedBytes);

    public void AddRecent(CopyIssue issue)
    {
        _recent.Enqueue(issue);
        while (_recent.Count > 200 && _recent.TryDequeue(out _)) { }
    }

    public IReadOnlyList<CopyIssue> RecentIssues() => _recent.Reverse().ToList();
}

public enum RestoreOutcome { Completed, Cancelled, Failed }

public sealed class RestoreItemResult(RestoreItem item)
{
    public RestoreItem Item { get; } = item;
    public bool Started;
    public long Restored;
    public long Replaced;
    public long WrittenBytes;
    public int Kept;
    public int Identical;
    public int ErrorFiles;
    public int ErrorFolders;
    public int Skipped;
    public int Interrupted;
    /// <summary>Firefox: outcome of the profiles.ini registration.</summary>
    public string? Registration;
    public long Written => Restored + Replaced;
    public int Errors => ErrorFiles + ErrorFolders;
}

/// <summary>Result of a restore. The final screen and the log read the same object.</summary>
public sealed class RestoreResult
{
    public RestoreOutcome Outcome { get; set; }
    public DateTime StartedAt { get; init; }
    public TimeSpan Duration { get; set; }
    public string? FatalMessage { get; set; }
    public string? LogPath { get; set; }
    public string SafetyRoot { get; init; } = "";
    public bool SafetyUsed { get; set; }

    /// <summary>Exact pre-scan made when the restore started (same rules as the summary).</summary>
    public RestoreAnalysis? Analysis { get; set; }
    public long SelectedFiles => Analysis?.Files ?? 0;
    public long PlannedFiles => Analysis?.WriteFiles ?? 0;
    public long PlannedBytes => Analysis?.WriteBytes ?? 0;

    public long RestoredFiles { get; set; }
    public long ReplacedFiles { get; set; }
    public long WrittenBytes { get; set; }
    public int KeptFiles { get; set; }
    public int IdenticalFiles { get; set; }
    public int ErrorFiles { get; set; }
    public int ErrorFolders { get; set; }
    public int SkippedCount { get; set; }
    public int InterruptedFiles { get; set; }

    public long WrittenFiles => RestoredFiles + ReplacedFiles;
    public int ErrorCount => ErrorFiles + ErrorFolders;
    public long ProcessedFiles => WrittenFiles + ErrorFiles + InterruptedFiles;
    public long NotProcessedFiles => Outcome == RestoreOutcome.Completed ? 0 : Math.Max(0, PlannedFiles - ProcessedFiles);

    public List<RestoreItemResult> Items { get; } = [];
    public List<WifiRestoreResult> Wifi { get; } = [];
    public WallpaperRestoreResult? Wallpaper { get; set; }
    public Dictionary<IssueKind, int> IssueCounts { get; } = [];
    public int Count(IssueKind kind) => IssueCounts.TryGetValue(kind, out var n) ? n : 0;

    public bool HasWarnings => ErrorCount > 0 || Wifi.Any(w => w.Outcome is WifiRestoreOutcome.NotRestored or WifiRestoreOutcome.Failed)
                               || Wallpaper is { Applied: false, ApplyRequested: true } || SkippedCount > 0 || InterruptedFiles > 0;
}
