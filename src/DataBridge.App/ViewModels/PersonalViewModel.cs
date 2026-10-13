using System.Collections;
using System.Windows.Input;
using System.Windows.Threading;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

/// <summary>
/// Personal folders + internal explorer. The tree is a flat, virtualized list: a folder is listed only when it is
/// opened (lazy loading), sizes arrive from the background scanner, and every checkbox reads/writes the
/// UI-independent <see cref="SelectionTree"/> of its personal folder. The same explorer serves the backup (folders of
/// this PC) and the restore (folders of the backup, restored into this PC's Known Folders).
/// </summary>
public sealed class PersonalViewModel : WizardPage
{
    private readonly IExplorerHost _host;
    private const int MaxResults = 2000;

    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private string _searchText = "";
    private bool _largeOnly;
    private bool _isSearchMode;
    private bool _isSearching;
    private string _searchStatus = "";

    public PersonalViewModel(IExplorerHost host, IReadOnlyList<PersonalRootViewModel> roots, int number, string automationId, string subtitle, bool isRestore = false)
        : base(number, "Dossiers personnels", "", automationId)
    {
        _host = host;
        Subtitle = subtitle;
        IsRestore = isRestore;
        Roots = roots;
        foreach (var root in Roots)
        {
            var row = new TreeRowViewModel(this, root, null, 0) { State = root.State };
            Rows.Add(row);
        }

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); StartSearch(); };

        QuickSearchCommand = new RelayCommand(p => SearchText = p as string ?? "");
        ClearSearchCommand = new RelayCommand(ClearSearch);
        CheckItemsCommand = new RelayCommand(p => ApplyToItems(p as IList, true));
        UncheckItemsCommand = new RelayCommand(p => ApplyToItems(p as IList, false));
        SelectAllCommand = new RelayCommand(() => SetAllRoots(true));
        SelectNoneCommand = new RelayCommand(() => SetAllRoots(false));
        RefreshCommand = new RelayCommand(Refresh);
    }

    public IReadOnlyList<PersonalRootViewModel> Roots { get; }
    public string PageTitle => Title;
    public string Subtitle { get; }
    public bool IsRestore { get; }

    /// <summary>Restore: shown when a selected folder goes to a OneDrive folder of this PC.</summary>
    public bool ShowOneDriveNotice => IsRestore && Roots.Any(r => r.IsOneDrive && r.Available && r.State != CheckState.Unchecked);
    public RangeObservableCollection<TreeRowViewModel> Rows { get; } = [];
    public RangeObservableCollection<SearchResultViewModel> Results { get; } = [];

    public ICommand QuickSearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand CheckItemsCommand { get; }
    public ICommand UncheckItemsCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand RefreshCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public bool LargeOnly
    {
        get => _largeOnly;
        set
        {
            if (!Set(ref _largeOnly, value)) return;
            StartSearch();
        }
    }

    public bool IsSearchMode { get => _isSearchMode; private set => Set(ref _isSearchMode, value); }
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }
    public string SearchStatus { get => _searchStatus; private set => Set(ref _searchStatus, value); }

    // ------------------------------------------------------------------ tree

    private Func<string, IEnumerable<string>?> ListSelectableChildren => dir =>
        _host.Listings.Get(dir).Entries.Where(e => !e.IsLink && BoundaryLabelFor(e.FullPath) is null).Select(e => e.FullPath);

    /// <summary>Label of the personal folder handled separately when <paramref name="path"/> is one (nested Known Folder).</summary>
    private string? BoundaryLabelFor(string path) =>
        Roots.FirstOrDefault(r => r.Available && PathUtil.AreSame(r.Model.Path, path))?.Label;

    public async void Expand(TreeRowViewModel row)
    {
        if (row.Children is null)
        {
            row.IsLoading = true;
            var listing = await Task.Run(() => _host.Listings.Get(row.FullPath));
            row.IsLoading = false;
            if (!row.IsExpanded) return; // collapsed while loading

            if (listing.Failed)
                row.Error = listing.Error == 5 ? "Accès refusé avec les droits de la session" : $"Lecture impossible (erreur {listing.Error})";

            var snapshot = row.Root.Model.Tree!.Snapshot();
            var children = listing.Entries
                .Where(e => !e.IsLink)
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, Comparer<string>.Create(NaturalSort.Compare))
                .Select(e =>
                {
                    var child = new TreeRowViewModel(this, row.Root, e, row.Depth + 1, e.IsDirectory ? BoundaryLabelFor(e.FullPath) : null);
                    child.State = child.IsBoundary ? CheckState.Unchecked : snapshot.StateOf(e.FullPath);
                    if (e.IsDirectory && !child.IsBoundary)
                    {
                        if (_host.Scanner.TryGet(e.FullPath, out var stats)) child.SetStats(stats);
                        else _host.Scanner.Enqueue(e.FullPath, priority: true);
                    }
                    return child;
                })
                .ToList();
            row.Children = children;
            if (children.Count == 0 && !listing.Failed) row.HasChildren = false;
        }

        var index = Rows.IndexOf(row);
        if (index < 0) return;
        var visible = new List<TreeRowViewModel>();
        AppendVisible(row.Children, visible);
        Rows.InsertRange(index + 1, visible);
    }

    private static void AppendVisible(IEnumerable<TreeRowViewModel> rows, List<TreeRowViewModel> output)
    {
        foreach (var r in rows)
        {
            output.Add(r);
            if (r.IsExpanded && r.Children != null) AppendVisible(r.Children, output);
        }
    }

    public void Collapse(TreeRowViewModel row)
    {
        var index = Rows.IndexOf(row);
        if (index < 0) return;
        var count = 0;
        while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > row.Depth) count++;
        if (count > 0) Rows.RemoveRange(index + 1, count);
    }

    // ------------------------------------------------------------------ selection

    public void Toggle(TreeRowViewModel row)
    {
        if (!row.CanToggle) return;
        Apply([(row.Root, row.FullPath)], row.State != CheckState.Checked);
    }

    public void ToggleResult(SearchResultViewModel result) =>
        Apply([(result.Root, result.FullPath)], result.State != CheckState.Checked);

    /// <summary>Checks or unchecks several selected rows at once (multi-selection, Space key, context menu).</summary>
    public void ApplyToItems(IList? items, bool include)
    {
        if (items is null) return;
        var targets = new List<(PersonalRootViewModel, string)>();
        foreach (var item in items)
        {
            switch (item)
            {
                case TreeRowViewModel { CanToggle: true } row: targets.Add((row.Root, row.FullPath)); break;
                case SearchResultViewModel result: targets.Add((result.Root, result.FullPath)); break;
            }
        }
        Apply(targets, include);
    }

    /// <summary>Space key: the new state follows the first item (partial/unchecked → checked, checked → unchecked).</summary>
    public void ToggleItems(IList? items)
    {
        if (items is null || items.Count == 0) return;
        var first = items[0] switch
        {
            TreeRowViewModel r => r.State,
            SearchResultViewModel s => s.State,
            _ => CheckState.Checked,
        };
        ApplyToItems(items, first != CheckState.Checked);
    }

    private void Apply(IReadOnlyCollection<(PersonalRootViewModel Root, string Path)> targets, bool include)
    {
        if (targets.Count == 0) return;
        foreach (var group in targets.Where(t => t.Root.Available).GroupBy(t => t.Root))
            group.Key.Model.Tree!.SetMany(group.Select(g => g.Path), include, ListSelectableChildren);
        RefreshStates();
        _host.ExplorerSelectionChanged();
    }

    private void SetAllRoots(bool include)
    {
        foreach (var root in Roots.Where(r => r.Available)) root.Model.Tree!.SetAll(include);
        RefreshStates();
        _host.ExplorerSelectionChanged();
    }

    public void RefreshStates()
    {
        var snapshots = Roots.Where(r => r.Available).ToDictionary(r => r, r => r.Model.Tree!.Snapshot());
        foreach (var root in Roots) root.State = root.Model.State;
        foreach (var row in Rows)
        {
            if (row.IsBoundary || !snapshots.TryGetValue(row.Root, out var snap)) continue;
            row.State = snap.StateOf(row.FullPath);
        }
        // Rows hidden under collapsed folders keep their cached state: refresh them too.
        foreach (var row in Rows.Where(r => r.Children != null && !r.IsExpanded).SelectMany(r => Descendants(r)))
            if (!row.IsBoundary && snapshots.TryGetValue(row.Root, out var snap)) row.State = snap.StateOf(row.FullPath);
        foreach (var result in Results)
            if (snapshots.TryGetValue(result.Root, out var snap))
                result.State = result.IsDirectory ? snap.StateOf(result.FullPath) : snap.IsIncluded(result.FullPath) ? CheckState.Checked : CheckState.Unchecked;
        UpdateSidebar();
        OnPropertyChanged(nameof(ShowOneDriveNotice));
    }

    private static IEnumerable<TreeRowViewModel> Descendants(TreeRowViewModel row)
    {
        if (row.Children is null) yield break;
        foreach (var c in row.Children)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // ------------------------------------------------------------------ sizes

    public override void OnTick()
    {
        foreach (var row in Rows)
        {
            if (!row.IsDirectory || row.IsBoundary || row.IsRoot || row.Size != null) continue;
            if (_host.Scanner.TryGet(row.FullPath, out var stats)) row.SetStats(stats);
        }
    }

    public void UpdateRootStatus()
    {
        foreach (var root in Roots)
            if (root.Stats is null && root.Available && _host.Scanner.TryGet(root.Model.Path, out var stats))
                root.Stats = stats;
        UpdateSidebar();
    }

    public void ApplyTotals(PlanTotals totals)
    {
        foreach (var root in Roots)
        {
            var job = totals.PerJob.Keys.FirstOrDefault(j => j.Category == Categories.Personal && PathUtil.AreSame(j.SourcePath, root.Model.Path));
            root.Selected = job != null ? totals.PerJob[job] : Totals.Zero;
        }
        UpdateSidebar();
    }

    private void UpdateSidebar()
    {
        var selected = Roots.Count(r => r.Available && r.State != CheckState.Unchecked);
        var partial = Roots.Count(r => r.State == CheckState.Indeterminate);
        var bytes = Roots.Where(r => r.Selected is { Complete: true }).Sum(r => r.Selected!.Value.Forecast(_host.SkipCloudOnly).PlannedCopyBytes);
        var allDone = Roots.Where(r => r.Available).All(r => r.Selected is { Complete: true });
        Status = Roots.Count(r => r.Available) == 0 ? (IsRestore ? "Absents de la sauvegarde" : "Aucun dossier")
                 : $"{selected}/{Roots.Count(r => r.Available)} {(IsRestore ? "sélectionnés" : "dossiers")}" + (partial > 0 ? $" ({partial} partiel{(partial > 1 ? "s" : "")})" : "")
                 + (allDone ? $" · {Format.Size(bytes)}" : " · analyse…");
        IsDone = selected > 0;
    }

    private void Refresh()
    {
        _host.Scanner.Invalidate();
        _host.Listings.Invalidate();
        foreach (var root in Roots)
        {
            root.Stats = null;
            if (root.Available) _host.Scanner.Enqueue(root.Model.Path);
        }
        var rootRows = Rows.Where(r => r.IsRoot).ToList();
        foreach (var r in rootRows)
        {
            r.SetExpandedSilently(false);
            r.Children = null;
            r.HasChildren = r.Root.Available;
        }
        Rows.ReplaceAll(rootRows);
        RefreshStates();
        _host.ExplorerSelectionChanged();
        if (IsSearchMode) StartSearch();
    }

    // ------------------------------------------------------------------ search

    private void ClearSearch()
    {
        _searchDebounce.Stop();
        _largeOnly = false;
        _searchText = "";
        OnPropertyChanged(nameof(LargeOnly));
        OnPropertyChanged(nameof(SearchText));
        StartSearch(); // no criteria: leaves search mode
    }

    private void StartSearch()
    {
        _searchCts?.Cancel();
        var terms = SearchText.Split([' ', ';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimStart('*')).Where(t => t.Length > 0).ToArray();
        if (terms.Length == 0 && !LargeOnly)
        {
            IsSearchMode = false;
            IsSearching = false;
            Results.ReplaceAll([]);
            SearchStatus = "";
            return;
        }

        IsSearchMode = true;
        IsSearching = true;
        Results.ReplaceAll([]);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var largeOnly = LargeOnly;
        var roots = Roots.Where(r => r.Available).ToList();
        var boundaries = roots.Select(r => r.Model.Path).ToList();
        var dispatcher = Dispatcher.CurrentDispatcher;
        SearchStatus = "Recherche…";

        Task.Run(() =>
        {
            var batch = new List<SearchResultViewModel>();
            long scanned = 0;
            var found = 0;
            var lastFlush = Environment.TickCount64;

            void Flush(bool final)
            {
                var items = batch.ToList();
                batch.Clear();
                var s = scanned;
                var f = found;
                dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    foreach (var item in items)
                    {
                        var snap = item.Root.Model.Tree!.Snapshot();
                        item.State = item.IsDirectory ? snap.StateOf(item.FullPath) : snap.IsIncluded(item.FullPath) ? CheckState.Checked : CheckState.Unchecked;
                        Results.Add(item);
                    }
                    SearchStatus = final
                        ? $"{Format.Plural(f, "résultat", "résultats")}" + (f >= MaxResults ? $" (limité à {Format.Count(MaxResults)}, affinez la recherche)" : "") + $" · {Format.Count(s)} éléments parcourus"
                        : $"Recherche… {Format.Count(s)} éléments parcourus, {Format.Count(f)} trouvés";
                    if (final) IsSearching = false;
                });
            }

            foreach (var root in roots)
            {
                var stack = new Stack<string>();
                stack.Push(root.Model.Path);
                while (stack.Count > 0 && !cts.IsCancellationRequested && found < MaxResults)
                {
                    var dir = stack.Pop();
                    IEnumerable<FsEntry> entries;
                    try { entries = FastDirectory.Enumerate(dir).ToList(); }
                    catch { continue; }
                    foreach (var e in entries)
                    {
                        if (e.IsLink) continue;
                        scanned++;
                        if (e.IsDirectory)
                        {
                            if (!boundaries.Any(b => PathUtil.AreSame(b, e.FullPath))) stack.Push(e.FullPath);
                            if (largeOnly) continue;
                        }
                        else if (largeOnly && e.Size < Format.OneGiB) continue;

                        if (terms.Length > 0 && !terms.Any(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                        batch.Add(new SearchResultViewModel(this, root, e));
                        if (++found >= MaxResults) break;
                    }
                    if (Environment.TickCount64 - lastFlush > 150)
                    {
                        lastFlush = Environment.TickCount64;
                        Flush(false);
                    }
                }
            }
            if (!cts.IsCancellationRequested) Flush(true);
        }, cts.Token);
    }

    public override void OnActivated()
    {
        RefreshStates();
        UpdateRootStatus();
    }
}
