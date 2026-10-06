using DataBridge.Core.IO;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Copy;

/// <summary>
/// Raw size of a selection. <see cref="Files"/>/<see cref="Bytes"/> count every selected file, cloud-only ones
/// included; <see cref="CloudFiles"/>/<see cref="CloudBytes"/> are the part that is not physically on the disk.
/// What will actually be copied depends on the cloud option: see <see cref="Forecast"/>.
/// </summary>
public readonly record struct Totals(long Bytes, long Files, long CloudBytes, long CloudFiles, bool Complete)
{
    public static Totals Zero => new(0, 0, 0, 0, true);
    public static Totals Pending => new(0, 0, 0, 0, false);

    public static Totals operator +(Totals a, Totals b) =>
        new(a.Bytes + b.Bytes, a.Files + b.Files, a.CloudBytes + b.CloudBytes, a.CloudFiles + b.CloudFiles, a.Complete && b.Complete);

    public long LocalBytes => Bytes - CloudBytes;
    public long LocalFiles => Files - CloudFiles;

    public CopyForecast Forecast(bool skipCloudOnly) => skipCloudOnly
        ? new CopyForecast(Files, Bytes, LocalFiles, LocalBytes, CloudFiles, CloudBytes, Complete)
        : new CopyForecast(Files, Bytes, Files, Bytes, 0, 0, Complete);
}

/// <summary>
/// What the copy will do with the selection, before it starts.
/// A cloud-only file left on "do not download" is selected but neither planned nor ever counted as copied.
/// </summary>
public readonly record struct CopyForecast(
    long SelectedFileCount,
    long SelectedBytes,
    long PlannedCopyFileCount,
    long PlannedCopyBytes,
    long SkippedCloudOnlyCount,
    long SkippedCloudOnlyBytes,
    bool Complete);

public sealed record PlanTotals(IReadOnlyDictionary<CopyJob, Totals> PerJob, Totals Sum);

/// <summary>
/// Computes the selected size of copy jobs with the same rules as the copy engine. Unchanged subtrees use the
/// cached recursive stats of <see cref="SizeScanner"/>; only folders containing selection rules are listed.
/// Non-blocking mode never waits: missing stats are queued and the result is flagged incomplete.
/// Without scanner and listing cache (<see cref="CountNow"/>), everything is read straight from the disk.
/// </summary>
public sealed class PlanCalculator(SizeScanner? scanner, DirectoryListingCache? listings)
{
    private Action<long>? _onFiles;
    private long _filesSeen;

    /// <summary>
    /// Exact count read from the disk right now (no cache), with the copy engine's rules. Used when the copy starts
    /// so that the progress denominators are the files really planned at that moment.
    /// </summary>
    public static PlanTotals CountNow(CopyPlan plan, CancellationToken ct, Action<long>? onFiles = null) =>
        new PlanCalculator(null, null) { _onFiles = onFiles }.ForPlan(plan, blocking: true, ct);

    public PlanTotals ForPlan(CopyPlan plan, bool blocking, CancellationToken ct = default)
    {
        var perJob = new Dictionary<CopyJob, Totals>();
        var sum = Totals.Zero;
        foreach (var job in plan.Jobs)
        {
            ct.ThrowIfCancellationRequested();
            var t = ForJob(job, blocking, ct);
            perJob[job] = t;
            sum += t;
        }
        return new PlanTotals(perJob, sum);
    }

    public Totals ForJob(CopyJob job, bool blocking, CancellationToken ct = default)
    {
        if (job.IsFile)
        {
            if (job.Coverage?.Of(job.SourcePath, false) == Coverage.Full) return Totals.Zero;
            var entry = FastDirectory.GetEntry(job.SourcePath);
            // A file that vanished is still planned: the engine reports it as an error, so planned = copied + errors.
            if (entry is null) return new Totals(0, 1, 0, 0, true);
            if (entry.IsDirectory) return Totals.Zero;
            return FileTotals(entry);
        }

        var included = job.Selection?.IsIncluded(job.SourcePath) ?? true;
        var coverage = job.Coverage?.Of(job.SourcePath, true) ?? Coverage.None;
        return WalkDirectory(job, job.SourcePath, included, coverage, blocking, ct);
    }

    private Totals WalkDirectory(CopyJob job, string dir, bool included, Coverage coverage, bool blocking, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (coverage == Coverage.Full) return Totals.Zero;
        var selection = job.Selection;
        var rulesBelow = selection?.HasRulesBelow(dir) ?? false;
        if (!included && !rulesBelow) return Totals.Zero;

        if (scanner != null && coverage == Coverage.None && !rulesBelow)
        {
            // Whole subtree included: cached recursive stats.
            if (blocking) return FromStats(scanner.GetOrCompute(dir));
            if (scanner.TryGet(dir, out var stats)) return FromStats(stats);
            scanner.Enqueue(dir, priority: true);
            return Totals.Pending;
        }

        var entries = listings != null ? listings.Get(dir).Entries : FastDirectory.TryList(dir, out _) ?? [];
        var total = Totals.Zero;
        foreach (var e in entries)
        {
            if (e.IsLink) continue;
            var childIncluded = selection?.IsChildIncluded(e.FullPath, included) ?? included;
            var childCoverage = coverage == Coverage.None ? Coverage.None : job.Coverage!.Of(e.FullPath, e.IsDirectory);
            if (e.IsDirectory)
            {
                total += WalkDirectory(job, e.FullPath, childIncluded, childCoverage, blocking, ct);
            }
            else if (childIncluded && childCoverage != Coverage.Full)
            {
                total += FileTotals(e);
            }
        }
        return total;
    }

    private Totals FileTotals(FsEntry e)
    {
        if (_onFiles != null && ++_filesSeen % 2000 == 0) _onFiles(_filesSeen);
        return e.IsCloudOnly ? new Totals(e.Size, 1, e.Size, 1, true) : new Totals(e.Size, 1, 0, 0, true);
    }

    private static Totals FromStats(DirStats s) => new(s.Bytes, s.Files, s.CloudBytes, s.CloudFiles, true);
}
