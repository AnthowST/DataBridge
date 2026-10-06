using DataBridge.Core.IO;

namespace DataBridge.Core.Selection;

public enum CheckState { Unchecked, Checked, Indeterminate }

/// <summary>
/// Selection of a file-system subtree, independent from any UI.
/// It is stored as a default for the root plus explicit path rules (the most specific rule wins),
/// so a folder holding 100 000 files costs nothing until the technician changes something inside it.
/// Rules are kept canonical: a rule only exists where its value differs from its parent's effective value,
/// and when every child of a folder ends up in the same state the rules collapse onto the folder.
/// </summary>
public sealed class SelectionTree
{
    private readonly object _gate = new();
    private Dictionary<string, bool> _rules = new(PathUtil.Comparer);
    private bool _rootIncluded;
    private SelectionSnapshot? _snapshot;

    public SelectionTree(string rootPath, bool rootIncluded = true)
    {
        RootPath = PathUtil.Normalize(rootPath);
        _rootIncluded = rootIncluded;
    }

    public string RootPath { get; }

    /// <summary>Raised after every change (on the thread that made the change).</summary>
    public event EventHandler? Changed;

    /// <summary>Immutable view, safe to use from background threads (size calculation, copy engine).</summary>
    public SelectionSnapshot Snapshot()
    {
        lock (_gate)
            return _snapshot ??= new SelectionSnapshot(RootPath, _rootIncluded, _rules);
    }

    public CheckState StateOf(string path) => Snapshot().StateOf(path);

    public bool IsIncluded(string path) => Snapshot().IsIncluded(path);

    /// <summary>Includes or excludes everything at and beneath <paramref name="path"/>.</summary>
    /// <param name="listChildren">
    /// Returns the selectable children of a directory (full paths), or null when unknown. Used to collapse
    /// rules upward so that a folder whose children are all unchecked shows as unchecked (not partial).
    /// </param>
    public void Set(string path, bool include, Func<string, IEnumerable<string>?>? listChildren = null)
    {
        SetMany([path], include, listChildren);
    }

    /// <summary>Same as <see cref="Set"/> for several paths, raising <see cref="Changed"/> once.</summary>
    public void SetMany(IEnumerable<string> paths, bool include, Func<string, IEnumerable<string>?>? listChildren = null)
    {
        lock (_gate)
        {
            var rules = new Dictionary<string, bool>(_rules, PathUtil.Comparer);
            var rootIncluded = _rootIncluded;

            foreach (var raw in paths)
            {
                var path = PathUtil.Normalize(raw);
                if (!PathUtil.IsSameOrUnder(path, RootPath))
                    throw new ArgumentException($"{path} n'est pas dans {RootPath}", nameof(paths));

                Apply(rules, ref rootIncluded, path, include);
                if (listChildren != null) CollapseUpward(rules, ref rootIncluded, path, include, listChildren);
            }

            _rules = rules;
            _rootIncluded = rootIncluded;
            _snapshot = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects or clears the whole tree.</summary>
    public void SetAll(bool include) => Set(RootPath, include);

    /// <summary>
    /// Adds a raw rule without normalization. Used to build programmatic selections
    /// (e.g. a browser profile minus its cache folders).
    /// </summary>
    public void AddRule(string path, bool include)
    {
        lock (_gate)
        {
            var normalized = PathUtil.Normalize(path);
            if (PathUtil.AreSame(normalized, RootPath)) _rootIncluded = include;
            else _rules[normalized] = include;
            _snapshot = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Apply(Dictionary<string, bool> rules, ref bool rootIncluded, string path, bool include)
    {
        foreach (var key in rules.Keys.Where(k => PathUtil.IsSameOrUnder(k, path)).ToList())
            rules.Remove(key);

        if (PathUtil.AreSame(path, RootPath))
        {
            rootIncluded = include;
            return;
        }

        var inherited = new SelectionSnapshot(RootPath, rootIncluded, rules, computeAncestors: false).IsIncluded(PathUtil.Parent(path)!);
        if (inherited != include) rules[path] = include;
    }

    private void CollapseUpward(Dictionary<string, bool> rules, ref bool rootIncluded, string path, bool include,
        Func<string, IEnumerable<string>?> listChildren)
    {
        var wanted = include ? CheckState.Checked : CheckState.Unchecked;
        var current = path;
        while (!PathUtil.AreSame(current, RootPath))
        {
            var parent = PathUtil.Parent(current);
            if (parent is null) break;
            var children = listChildren(parent);
            if (children is null) break;

            var snapshot = new SelectionSnapshot(RootPath, rootIncluded, rules);
            var any = false;
            var uniform = true;
            foreach (var child in children)
            {
                any = true;
                if (snapshot.StateOf(child) != wanted) { uniform = false; break; }
            }
            if (!any || !uniform) break;

            Apply(rules, ref rootIncluded, parent, include);
            current = parent;
        }
    }
}

/// <summary>Immutable copy of a <see cref="SelectionTree"/>.</summary>
public sealed class SelectionSnapshot
{
    private readonly Dictionary<string, bool> _rules;
    private readonly HashSet<string> _ancestors = new(PathUtil.Comparer);

    internal SelectionSnapshot(string rootPath, bool rootIncluded, Dictionary<string, bool> rules, bool computeAncestors = true)
    {
        RootPath = rootPath;
        RootIncluded = rootIncluded;
        _rules = new Dictionary<string, bool>(rules, PathUtil.Comparer);
        if (!computeAncestors) return;
        foreach (var key in _rules.Keys)
        {
            var p = PathUtil.Parent(key);
            while (p != null && PathUtil.IsSameOrUnder(p, RootPath) && _ancestors.Add(p))
                p = PathUtil.Parent(p);
        }
    }

    public string RootPath { get; }
    public bool RootIncluded { get; }
    public IReadOnlyDictionary<string, bool> Rules => _rules;
    public bool HasRules => _rules.Count > 0;

    /// <summary>Effective inclusion of a path: nearest rule at or above it, otherwise the root default.</summary>
    public bool IsIncluded(string path)
    {
        var p = path;
        while (p != null)
        {
            if (_rules.TryGetValue(p, out var v)) return v;
            if (PathUtil.AreSame(p, RootPath)) return RootIncluded;
            p = PathUtil.Parent(p);
        }
        return RootIncluded;
    }

    /// <summary>Explicit rule on exactly this path, if any.</summary>
    public bool? RuleFor(string path) => _rules.TryGetValue(path, out var v) ? v : null;

    /// <summary>Inclusion of a child when the parent's effective inclusion is already known (fast path for walkers).</summary>
    public bool IsChildIncluded(string childPath, bool parentIncluded) =>
        _rules.TryGetValue(childPath, out var v) ? v : parentIncluded;

    /// <summary>True when some rule exists strictly beneath the directory (its content is mixed).</summary>
    public bool HasRulesBelow(string directory) => _ancestors.Contains(directory);

    public CheckState StateOf(string path) =>
        HasRulesBelow(path) ? CheckState.Indeterminate : IsIncluded(path) ? CheckState.Checked : CheckState.Unchecked;

    public CheckState RootState => StateOf(RootPath);
}
