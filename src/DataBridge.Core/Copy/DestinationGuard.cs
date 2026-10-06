using DataBridge.Core.IO;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Copy;

public enum OverlapKind { BackupInsideSource, SourceInsideBackup }

/// <summary>A source of the plan overlaps the backup folder: the copy would copy itself.</summary>
public sealed record DestinationConflict(string SourceLabel, string SourcePath, OverlapKind Kind)
{
    public const string Title = "Destination invalide";
    public const string Message = "Le dossier de sauvegarde se trouve à l'intérieur d'un dossier sélectionné pour la copie.";
    public const string Advice = "Choisissez une destination située en dehors des sources sauvegardées.";

    public string Detail => Kind == OverlapKind.BackupInsideSource
        ? $"Source concernée : {SourceLabel} ({SourcePath})"
        : $"La source {SourceLabel} ({SourcePath}) se trouve dans le dossier de sauvegarde.";

    public string FullText => $"{Title}\n\n{Message}\n\n{Advice}\n\n{Detail}";
}

/// <summary>
/// Refuses a backup folder located inside what is being copied (Known Folders, selected subfolders, additional
/// folders, browser data...), and a source located inside the backup folder. Paths are compared by identity
/// (<see cref="PathIdentity"/>): "..", case, "\\?\" prefixes, mapped drives versus UNC, junctions.
/// A destination inside a source is accepted only when the selection really leaves it out (e.g. Documents
/// selected but its "Sauvegardes" subfolder unchecked): then nothing can be copied recursively.
/// </summary>
public static class DestinationGuard
{
    public static OverlapKind? Overlap(string sourcePath, string backupRoot)
    {
        var source = PathIdentity.Canonical(sourcePath);
        var backup = PathIdentity.Canonical(backupRoot);
        if (PathUtil.IsSameOrUnder(backup, source)) return OverlapKind.BackupInsideSource;
        if (PathUtil.IsStrictlyUnder(source, backup)) return OverlapKind.SourceInsideBackup;
        return null;
    }

    public static DestinationConflict? Check(CopyPlan plan) => Check(plan.Jobs, plan.BackupRoot);

    public static DestinationConflict? Check(IEnumerable<CopyJob> jobs, string backupRoot)
    {
        var backup = PathIdentity.Canonical(backupRoot);
        foreach (var job in jobs)
        {
            var source = PathIdentity.Canonical(job.SourcePath);
            if (PathUtil.IsStrictlyUnder(source, backup))
                return new DestinationConflict(job.Label, job.SourcePath, OverlapKind.SourceInsideBackup);
            if (!PathUtil.IsSameOrUnder(backup, source)) continue;

            // Same location seen from the job (its selection rules are written with the job's own paths).
            var relative = PathUtil.Relative(backup, source);
            var inJob = relative.Length == 0 ? job.SourcePath : Path.Combine(job.SourcePath, relative);
            var included = job.Selection?.IsIncluded(inJob) ?? true;
            var handledByAnotherJob = job.HandledElsewhere?.Of(inJob, true) == Coverage.Full;
            if (included && !handledByAnotherJob)
                return new DestinationConflict(job.Label, job.SourcePath, OverlapKind.BackupInsideSource);
        }
        return null;
    }
}
