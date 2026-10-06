using DataBridge.Core.Copy;
using DataBridge.Core.Profile;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

public class PlanAndCopyTests
{
    private static ProfileInfo FakeProfile(string root) =>
        new("PC-TEST", "tech", "PC-TEST", root, root, root, null, "Windows test", ElevationState.Standard);

    private static (BackupSelection Selection, PersonalRoot Documents) SelectionWithDocuments(TempTree t)
    {
        var docs = t.Dir("Documents");
        var def = KnownFolders.Personal.First(d => d.Kind == PersonalFolderKind.Documents);
        var root = new PersonalRoot(new PersonalFolderInfo(def, docs, true, FolderLocation.Local, null));
        var selection = new BackupSelection { Profile = FakeProfile(t.Root) };
        selection.PersonalRoots.Add(root);
        return (selection, root);
    }

    private static Func<string, IEnumerable<string>?> Lister(DirectoryListingCache cache) =>
        dir => cache.Get(dir).Entries.Where(e => !e.IsLink).Select(e => e.FullPath);

    [Fact]
    public void Estimated_size_drops_when_a_big_file_is_unchecked()
    {
        using var t = new TempTree();
        var (selection, docs) = SelectionWithDocuments(t);
        var iso = t.File(@"Documents\Windows11.iso", 50_000);
        t.File(@"Documents\Client\file.xlsx", 1_000);
        using var scanner = new SizeScanner();
        var cache = new DirectoryListingCache();
        var calc = new PlanCalculator(scanner, cache);
        var dest = t.Dir("Dest");

        var before = calc.ForPlan(selection.BuildPlan(dest, "Backup"), blocking: true).Sum;
        docs.Tree!.Set(iso, false, Lister(cache));
        var after = calc.ForPlan(selection.BuildPlan(dest, "Backup"), blocking: true).Sum;

        Assert.Equal(51_000, before.Bytes);
        Assert.Equal(1_000, after.Bytes);
        Assert.Equal(1, after.Files);
    }

    [Fact]
    public void Manual_file_inside_selected_folder_is_reported_as_duplicate()
    {
        using var t = new TempTree();
        var (selection, docs) = SelectionWithDocuments(t);
        var test = t.File(@"Documents\Test.xlsx", 10);
        selection.Additional.Add(new AdditionalItem(test, false));

        var status = selection.AnalyzeAdditional().Single().Value;
        Assert.Equal(AdditionalStatusKind.AlreadyIncluded, status.Kind);
        Assert.Equal("Ce fichier est déjà inclus via Documents.", status.Message);

        // Once unchecked in Documents, the manual addition is copied on its own.
        docs.Tree!.Set(test, false);
        status = selection.AnalyzeAdditional().Single().Value;
        Assert.Equal(AdditionalStatusKind.Ready, status.Kind);
    }

    [Fact]
    public async Task Copy_respects_selection_and_never_duplicates()
    {
        using var t = new TempTree();
        var (selection, docs) = SelectionWithDocuments(t);
        var iso = t.File(@"Documents\Windows11.iso", 5_000);
        t.File(@"Documents\Client\file.xlsx", 100);
        var dup = t.File(@"Documents\Test.xlsx", 10);
        var extra = t.File(@"Elsewhere\config.xml", 20);
        t.File(@"Elsewhere\Compta\bilan.pdf", 30);
        var cache = new DirectoryListingCache();

        docs.Tree!.Set(iso, false, Lister(cache));
        selection.Additional.Add(new AdditionalItem(dup, false));          // already inside Documents
        selection.Additional.Add(new AdditionalItem(extra, false));
        selection.Additional.Add(new AdditionalItem(t.P(@"Elsewhere\Compta"), true));
        selection.Additional.Add(new AdditionalItem(t.Root, true));         // contains Documents: no duplicate

        // Destination outside every source (a destination inside one is blocked: see DestinationGuardTests).
        using var target = new TempTree();
        var dest = target.Dir("Dest");
        var plan = selection.BuildPlan(dest, "Backup");
        var progress = new CopyProgress();
        var result = await new CopyEngine(plan, progress).RunAsync(CancellationToken.None);

        var backup = Path.Combine(dest, "Backup");
        Assert.Equal(CopyOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(Path.Combine(backup, @"Personal\Documents\Client\file.xlsx")));
        Assert.True(File.Exists(Path.Combine(backup, @"Personal\Documents\Test.xlsx")));
        Assert.False(File.Exists(Path.Combine(backup, @"Personal\Documents\Windows11.iso")));
        Assert.False(File.Exists(Path.Combine(backup, @"Additional\Test.xlsx")));
        // The root folder added manually contains Documents, handled in Personal: not copied twice.
        var rootCopy = Directory.GetDirectories(Path.Combine(backup, "Additional")).Single(d => !d.EndsWith("Compta"));
        Assert.False(Directory.Exists(Path.Combine(rootCopy, "Documents")));
        Assert.True(File.Exists(Path.Combine(rootCopy, @"Elsewhere\config.xml")));
        Assert.True(File.Exists(Path.Combine(backup, CopyLogWriterName)));
        Assert.Empty(Directory.GetFiles(backup, ".copy-log-*.tmp"));
    }

    private const string CopyLogWriterName = "copy-log.txt";

    [Fact]
    public async Task Cancel_keeps_already_copied_files_and_writes_log()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        for (var i = 0; i < 50; i++) t.File($@"Documents\f{i:00}.bin", 1000);
        var dest = t.Dir("Dest");
        var plan = selection.BuildPlan(dest, "Backup");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await new CopyEngine(plan, new CopyProgress()).RunAsync(cts.Token);

        Assert.Equal(CopyOutcome.Cancelled, result.Outcome);
        var log = File.ReadAllText(Path.Combine(dest, "Backup", CopyLogWriterName));
        Assert.Contains("Copie interrompue par l'utilisateur", log);
    }

    [Fact]
    public void Folder_name_is_valid_for_windows()
    {
        var name = BackupNaming.FolderName("PC-CLIENT", "AST:UC*KI", new DateTime(2026, 10, 5, 22, 45, 0));
        Assert.Equal("DataBridge_PC-CLIENT_AST-UC-KI_2026-10-05_22-45", name);
    }

    [Theory]
    [InlineData(867_328L, "847 Ko")]
    [InlineData(131_072_000L, "125 Mo")]
    [InlineData(2_576_980_378L, "2.4 Go")]
    [InlineData(11_059_540_787L, "10.3 Go")]
    public void Sizes_are_human_readable(long bytes, string expected) => Assert.Equal(expected, DataBridge.Core.Format.Size(bytes));
}
