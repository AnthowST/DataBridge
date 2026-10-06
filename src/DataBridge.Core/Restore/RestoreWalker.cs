using DataBridge.Core.Copy;
using DataBridge.Core.IO;

namespace DataBridge.Core.Restore;

internal enum WalkKind { EnterDirectory, File, Skipped, Error }

/// <summary>One step of a restore item: a destination folder to create, a file to decide on, or something refused.</summary>
internal readonly record struct WalkEvent(
    WalkKind Kind,
    string SourcePath,
    string DestinationPath,
    string Relative,
    FsEntry? Source,
    FsEntry? Existing,
    IssueKind Issue,
    string? Message);

/// <summary>
/// Walks one restore item exactly the same way for the summary analysis and for the engine (same selection, same
/// exclusions, same safety rules), streaming one directory at a time. Backup content is untrusted: links are never
/// followed, unsafe names are refused, and every destination is checked to stay under the item's allowed root.
/// </summary>
internal static class RestoreWalker
{
    private readonly record struct Frame(string Source, string Destination, string Relative, bool Included);

    public static IEnumerable<WalkEvent> Walk(RestoreItem item, CancellationToken ct)
    {
        if (item.IsFile)
        {
            foreach (var e in WalkFile(item)) yield return e;
            yield break;
        }

        var selection = item.Selection;
        var stack = new Stack<Frame>();
        stack.Push(new Frame(item.SourcePath, item.DestinationPath, "", selection?.IsIncluded(item.SourcePath) ?? true));
        var subdirs = new List<Frame>();

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frame = stack.Pop();
            var rulesBelow = selection?.HasRulesBelow(frame.Source) ?? false;
            if (!frame.Included && !rulesBelow) continue;

            if (frame.Included)
                yield return new WalkEvent(WalkKind.EnterDirectory, frame.Source, frame.Destination, frame.Relative, null, null, IssueKind.Other, null);

            var sourceEntries = FastDirectory.TryList(frame.Source, out var error);
            if (sourceEntries is null)
            {
                yield return new WalkEvent(WalkKind.Error, frame.Source, frame.Destination, frame.Relative, null, null,
                    error == 5 ? IssueKind.AccessDenied : IssueKind.ReadError, $"Dossier de la sauvegarde illisible (erreur {error}).");
                continue;
            }

            // The destination folder is listed once: existing files are matched by name (no per-file disk access).
            Dictionary<string, FsEntry>? existing = null;
            if (Directory.Exists(frame.Destination) && FastDirectory.TryList(frame.Destination, out _) is { } destEntries)
            {
                existing = new Dictionary<string, FsEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in destEntries) existing[d.Name] = d;
            }

            subdirs.Clear();
            foreach (var e in sourceEntries)
            {
                var relative = frame.Relative.Length == 0 ? e.Name : frame.Relative + "\\" + e.Name;
                if (item.IsExcluded(relative)) continue;
                var included = selection?.IsChildIncluded(e.FullPath, frame.Included) ?? frame.Included;
                if (!included && !(e.IsDirectory && (selection?.HasRulesBelow(e.FullPath) ?? false))) continue;

                if (!RestorePaths.IsSafeName(e.Name))
                {
                    yield return new WalkEvent(WalkKind.Skipped, e.FullPath, frame.Destination, relative, e, null, IssueKind.UnsafePath,
                        "Nom refusé par sécurité (caractères ou nom réservé) : non restauré.");
                    continue;
                }
                if (e.IsLink)
                {
                    yield return new WalkEvent(WalkKind.Skipped, e.FullPath, frame.Destination, relative, e, null, IssueKind.Link,
                        "Lien symbolique / jonction dans la sauvegarde : jamais suivi.");
                    continue;
                }

                var destination = Path.Combine(frame.Destination, e.Name);
                if (!RestorePaths.IsInside(destination, item.AllowedRoot))
                {
                    yield return new WalkEvent(WalkKind.Skipped, e.FullPath, destination, relative, e, null, IssueKind.UnsafePath,
                        "Destination hors du dossier prévu : refusée par sécurité.");
                    continue;
                }

                FsEntry? current = null;
                existing?.TryGetValue(e.Name, out current);

                if (e.IsDirectory)
                {
                    if (current is { IsDirectory: false })
                    {
                        yield return new WalkEvent(WalkKind.Error, e.FullPath, destination, relative, e, current, IssueKind.WriteError,
                            "Un fichier du même nom existe à la destination : dossier non restauré.");
                        continue;
                    }
                    if (current is { IsLink: true })
                    {
                        yield return new WalkEvent(WalkKind.Skipped, e.FullPath, destination, relative, e, current, IssueKind.Link,
                            "Le dossier de destination est un lien / une jonction : non suivi, contenu non restauré.");
                        continue;
                    }
                    subdirs.Add(new Frame(e.FullPath, destination, relative, included));
                    continue;
                }

                if (current is { IsDirectory: true })
                {
                    yield return new WalkEvent(WalkKind.Error, e.FullPath, destination, relative, e, current, IssueKind.WriteError,
                        "Un dossier du même nom existe à la destination : fichier non restauré.");
                    continue;
                }
                if (current is { IsLink: true })
                {
                    yield return new WalkEvent(WalkKind.Skipped, e.FullPath, destination, relative, e, current, IssueKind.Link,
                        "Le fichier de destination est un lien : non remplacé.");
                    continue;
                }
                yield return new WalkEvent(WalkKind.File, e.FullPath, destination, relative, e, current, IssueKind.Other, null);
            }

            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
    }

    private static IEnumerable<WalkEvent> WalkFile(RestoreItem item)
    {
        var source = FastDirectory.GetEntry(item.SourcePath);
        var name = Path.GetFileName(item.DestinationPath);
        if (source is null || source.IsDirectory)
        {
            yield return new WalkEvent(WalkKind.Error, item.SourcePath, item.DestinationPath, name, null, null, IssueKind.NotFound,
                "Fichier absent de la sauvegarde.");
            yield break;
        }
        if (source.IsLink)
        {
            yield return new WalkEvent(WalkKind.Skipped, item.SourcePath, item.DestinationPath, name, source, null, IssueKind.Link,
                "Lien symbolique dans la sauvegarde : jamais suivi.");
            yield break;
        }
        var existing = FastDirectory.GetEntry(item.DestinationPath);
        if (existing is { IsDirectory: true })
        {
            yield return new WalkEvent(WalkKind.Error, item.SourcePath, item.DestinationPath, name, source, existing, IssueKind.WriteError,
                "Un dossier du même nom existe à la destination : fichier non restauré.");
            yield break;
        }
        yield return new WalkEvent(WalkKind.EnterDirectory, PathUtil.Parent(item.SourcePath)!, PathUtil.Parent(item.DestinationPath)!, "", null, null, IssueKind.Other, null);
        yield return new WalkEvent(WalkKind.File, item.SourcePath, item.DestinationPath, name, source, existing, IssueKind.Other, null);
    }
}
