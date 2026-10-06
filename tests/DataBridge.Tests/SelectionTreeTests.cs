using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

public class SelectionTreeTests
{
    private static Func<string, IEnumerable<string>?> Lister(DirectoryListingCache cache) =>
        dir => cache.Get(dir).Entries.Where(e => !e.IsLink).Select(e => e.FullPath);

    [Fact]
    public void Unchecking_a_file_makes_parents_indeterminate()
    {
        using var t = new TempTree();
        var iso = t.File(@"Downloads\Windows11.iso", 10);
        t.File(@"Downloads\install.exe", 5);
        t.File(@"Downloads\Drivers\a.sys", 5);
        var tree = new SelectionTree(t.Root);
        var cache = new DirectoryListingCache();

        tree.Set(iso, false, Lister(cache));

        Assert.Equal(CheckState.Unchecked, tree.StateOf(iso));
        Assert.Equal(CheckState.Indeterminate, tree.StateOf(t.P("Downloads")));
        Assert.Equal(CheckState.Indeterminate, tree.StateOf(t.Root));
        Assert.Equal(CheckState.Checked, tree.StateOf(t.P(@"Downloads\Drivers")));
        Assert.True(tree.IsIncluded(t.P(@"Downloads\install.exe")));
    }

    [Fact]
    public void Unchecking_every_child_collapses_to_unchecked_parent()
    {
        using var t = new TempTree();
        var a = t.File(@"Dir\a.txt", 1);
        var b = t.File(@"Dir\b.txt", 1);
        t.File(@"other.txt", 1);
        var tree = new SelectionTree(t.Root);
        var cache = new DirectoryListingCache();

        tree.Set(a, false, Lister(cache));
        tree.Set(b, false, Lister(cache));

        Assert.Equal(CheckState.Unchecked, tree.StateOf(t.P("Dir")));
        Assert.Single(tree.Snapshot().Rules); // one rule on Dir, not two on the files
        Assert.Equal(CheckState.Indeterminate, tree.StateOf(t.Root));

        tree.Set(t.P("other.txt"), false, Lister(cache));
        Assert.Equal(CheckState.Unchecked, tree.StateOf(t.Root));
        Assert.Empty(tree.Snapshot().Rules);
    }

    [Fact]
    public void Rechecking_restores_a_fully_checked_tree()
    {
        using var t = new TempTree();
        var iso = t.File(@"Downloads\big.iso", 1);
        t.File(@"Downloads\small.txt", 1);
        var tree = new SelectionTree(t.Root);
        var cache = new DirectoryListingCache();

        tree.Set(iso, false, Lister(cache));
        tree.Set(iso, true, Lister(cache));

        Assert.Equal(CheckState.Checked, tree.StateOf(t.Root));
        Assert.Empty(tree.Snapshot().Rules);
    }

    [Fact]
    public void Including_a_file_inside_an_excluded_folder_works()
    {
        using var t = new TempTree();
        var keep = t.File(@"Old\keep.docx", 1);
        t.File(@"Old\drop.bin", 1);
        var tree = new SelectionTree(t.Root);
        var cache = new DirectoryListingCache();

        tree.Set(t.P("Old"), false, Lister(cache));
        tree.Set(keep, true, Lister(cache));

        Assert.True(tree.IsIncluded(keep));
        Assert.False(tree.IsIncluded(t.P(@"Old\drop.bin")));
        Assert.Equal(CheckState.Indeterminate, tree.StateOf(t.P("Old")));
    }
}
