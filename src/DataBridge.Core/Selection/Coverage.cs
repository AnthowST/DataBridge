using DataBridge.Core.IO;

namespace DataBridge.Core.Selection;

/// <summary>How much of a path is already handled elsewhere (copied by another job, or excluded on purpose).</summary>
public enum Coverage { None, Partial, Full }

/// <summary>
/// Answers "is this path already taken care of?" for the copy engine and the size calculator.
/// Partial means "look at the children": something beneath is covered.
/// </summary>
public interface ICoverage
{
    Coverage Of(string path, bool isDirectory);
    string Describe();
}

/// <summary>A whole subtree is handled elsewhere (nested personal folder, the backup folder itself...).</summary>
public sealed class SubtreeCoverage(string root, string reason) : ICoverage
{
    public string Root { get; } = root;

    public Coverage Of(string path, bool isDirectory)
    {
        if (PathUtil.IsSameOrUnder(path, Root)) return Coverage.Full;
        if (isDirectory && PathUtil.IsStrictlyUnder(Root, path)) return Coverage.Partial;
        return Coverage.None;
    }

    public string Describe() => $"{Root} ({reason})";
}

/// <summary>Paths already included by another selection (used for manually added items inside a personal folder).</summary>
public sealed class SelectionCoverage(SelectionSnapshot other, string label) : ICoverage
{
    public string Label { get; } = label;

    public Coverage Of(string path, bool isDirectory)
    {
        if (PathUtil.IsSameOrUnder(path, other.RootPath))
        {
            if (!isDirectory) return other.IsIncluded(path) ? Coverage.Full : Coverage.None;
            return other.StateOf(path) switch
            {
                CheckState.Checked => Coverage.Full,
                CheckState.Unchecked => Coverage.None,
                _ => Coverage.Partial,
            };
        }
        if (isDirectory && PathUtil.IsStrictlyUnder(other.RootPath, path))
            return other.RootState == CheckState.Unchecked ? Coverage.None : Coverage.Partial;
        return Coverage.None;
    }

    public string Describe() => $"déjà inclus via {Label}";
}

public sealed class CompositeCoverage : ICoverage
{
    private readonly IReadOnlyList<ICoverage> _items;

    private CompositeCoverage(IReadOnlyList<ICoverage> items) => _items = items;

    public static ICoverage? Combine(IEnumerable<ICoverage?> items)
    {
        var list = items.Where(i => i != null).Cast<ICoverage>().ToList();
        return list.Count switch { 0 => null, 1 => list[0], _ => new CompositeCoverage(list) };
    }

    public Coverage Of(string path, bool isDirectory)
    {
        var result = Coverage.None;
        foreach (var item in _items)
        {
            var c = item.Of(path, isDirectory);
            if (c == Coverage.Full) return Coverage.Full;
            if (c == Coverage.Partial) result = Coverage.Partial;
        }
        return result;
    }

    public string Describe() => string.Join("; ", _items.Select(i => i.Describe()));
}
