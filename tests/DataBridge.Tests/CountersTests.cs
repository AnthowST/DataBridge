using System.Globalization;
using System.Text.RegularExpressions;
using DataBridge.Core.Copy;
using DataBridge.Core.Profile;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

/// <summary>
/// V1.1: selected / planned / copied / cloud skipped / skipped / errors are separate counters.
/// Cloud-only files are simulated with FILE_ATTRIBUTE_OFFLINE (settable by a standard user, detected like
/// OneDrive "online-only" files): with "do not download" they are never planned nor counted as copied.
/// </summary>
public class CountersTests
{
    internal static ProfileInfo FakeProfile(string root) =>
        new("PC-TEST", "tech", "PC-TEST", root, root, root, null, "Windows test", ElevationState.Standard);

    internal static (BackupSelection Selection, PersonalRoot Documents) SelectionWithDocuments(TempTree t)
    {
        var docs = t.Dir("Documents");
        var def = KnownFolders.Personal.First(d => d.Kind == PersonalFolderKind.Documents);
        var root = new PersonalRoot(new PersonalFolderInfo(def, docs, true, FolderLocation.Local, null));
        var selection = new BackupSelection { Profile = FakeProfile(t.Root) };
        selection.PersonalRoots.Add(root);
        return (selection, root);
    }

    private static void MakeFiles(TempTree t, int local, int cloud)
    {
        for (var i = 0; i < local; i++) t.File($@"Documents\Local\l{i:000}.bin", 1_000);
        for (var i = 0; i < cloud; i++)
        {
            var f = t.File($@"Documents\iCloud\c{i:000}.jpg", 5_000);
            File.SetAttributes(f, FileAttributes.Offline);
        }
    }

    private static async Task<(CopyResult Result, string Backup)> Copy(BackupSelection selection, TempTree t)
    {
        var dest = t.Dir("Dest");
        var plan = selection.BuildPlan(dest, "Backup");
        var result = await new CopyEngine(plan, new CopyProgress()).RunAsync(CancellationToken.None);
        return (result, Path.Combine(dest, "Backup"));
    }

    private static int FilesOnDisk(string backup) =>
        Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Count(f => !f.EndsWith("copy-log.txt") && !f.EndsWith("databridge-manifest.json"));

    [Fact]
    public async Task Ten_local_files_planned_10_copied_10()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 0);

        var (r, backup) = await Copy(selection, t);

        Assert.Equal(CopyOutcome.Completed, r.Outcome);
        Assert.Equal(10, r.SelectedFileCount);
        Assert.Equal(10, r.PlannedCopyFileCount);
        Assert.Equal(10, r.CopiedFileCount);
        Assert.Equal(10_000, r.CopiedBytes);
        Assert.Equal(0, r.SkippedCloudOnlyCount);
        Assert.Equal(0, r.ErrorFileCount);
        Assert.Equal(10, FilesOnDisk(backup));
        Assert.Equal(100, r.ProcessedPercent);
    }

    [Fact]
    public async Task Cloud_only_files_not_downloaded_are_selected_but_never_planned_nor_copied()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 90);
        selection.SkipCloudOnly = true;

        var (r, backup) = await Copy(selection, t);

        Assert.Equal(CopyOutcome.Completed, r.Outcome);
        Assert.Equal(100, r.SelectedFileCount);
        Assert.Equal(10, r.PlannedCopyFileCount);
        Assert.Equal(10_000, r.PlannedCopyBytes);
        Assert.Equal(10, r.CopiedFileCount);
        Assert.Equal(10_000, r.CopiedBytes);
        Assert.Equal(90, r.SkippedCloudOnlyCount);
        Assert.Equal(450_000, r.SkippedCloudOnlyBytes);
        Assert.Equal(0, r.ErrorFileCount);
        Assert.Equal(10, FilesOnDisk(backup));
        Assert.Equal(r.PlannedCopyFileCount, r.CopiedFileCount + r.ErrorFileCount);
        Assert.Equal(100, r.ProcessedPercent);
    }

    [Fact]
    public async Task Cloud_only_files_downloaded_are_planned_and_copied_when_accessible()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 90);
        selection.SkipCloudOnly = false;

        var (r, backup) = await Copy(selection, t);

        // The simulated "online-only" files are readable here: all of them are really copied.
        Assert.Equal(CopyOutcome.Completed, r.Outcome);
        Assert.Equal(100, r.SelectedFileCount);
        Assert.Equal(100, r.PlannedCopyFileCount);
        Assert.Equal(460_000, r.PlannedCopyBytes);
        Assert.Equal(100, r.CopiedFileCount);
        Assert.Equal(0, r.SkippedCloudOnlyCount);
        Assert.Equal(0, r.ErrorFileCount);
        Assert.Equal(100, FilesOnDisk(backup));
    }

    [Fact]
    public async Task One_failing_file_among_ten_planned_10_copied_9_error_1()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 0);
        var locked = t.P(@"Documents\Local\l005.bin");

        CopyResult r;
        string backup;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) // exclusive lock by "another program"
            (r, backup) = await Copy(selection, t);

        Assert.Equal(CopyOutcome.Completed, r.Outcome);
        Assert.Equal(10, r.PlannedCopyFileCount);
        Assert.Equal(9, r.CopiedFileCount);
        Assert.Equal(1, r.ErrorFileCount);
        Assert.Equal(1, r.LockedFileCount);
        Assert.Equal(0, r.ErrorFolderCount);
        Assert.Equal(9, FilesOnDisk(backup));
        Assert.Equal(r.PlannedCopyFileCount, r.CopiedFileCount + r.ErrorFileCount);
        Assert.Equal(100, r.ProcessedPercent); // every planned file was processed
    }

    [Fact]
    public async Task Log_counters_match_the_result_shown_in_the_interface()
    {
        using var t = new TempTree();
        var (selection, _) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 90);
        var locked = t.P(@"Documents\Local\l003.bin");

        CopyResult r;
        string backup;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            (r, backup) = await Copy(selection, t);

        var log = File.ReadAllText(Path.Combine(backup, "copy-log.txt"));
        long Value(string label)
        {
            var m = Regex.Match(log, "^" + Regex.Escape(label) + @"\s*: ([\d   ]+)", RegexOptions.Multiline);
            Assert.True(m.Success, "Ligne absente du journal : " + label);
            return long.Parse(Regex.Replace(m.Groups[1].Value, @"[   ]", ""), CultureInfo.InvariantCulture);
        }

        Assert.Equal(r.SelectedFileCount, Value("Fichiers sélectionnés"));
        Assert.Equal(r.PlannedCopyFileCount, Value("Fichiers prévus pour la copie"));
        Assert.Equal(r.CopiedFileCount, Value("Fichiers copiés"));
        Assert.Equal(r.SkippedCloudOnlyCount, Value("Fichiers cloud non téléchargés"));
        Assert.Equal(r.ErrorFileCount, Value("Fichiers en erreur"));
        Assert.Equal((100, 10, 9, 90, 1), (Value("Fichiers sélectionnés"), Value("Fichiers prévus pour la copie"),
            Value("Fichiers copiés"), Value("Fichiers cloud non téléchargés"), Value("Fichiers en erreur")));
        Assert.Contains("prévus 10 = copiés 9 + en erreur 1", log);
    }

    [Fact]
    public async Task Summary_forecast_equals_the_exact_count_made_by_the_copy()
    {
        using var t = new TempTree();
        var (selection, docs) = SelectionWithDocuments(t);
        MakeFiles(t, 10, 90);
        var big = t.File(@"Documents\Local\big.iso", 50_000);
        var cache = new DirectoryListingCache();
        docs.Tree!.Set(big, false, dir => cache.Get(dir).Entries.Select(e => e.FullPath));

        using var scanner = new SizeScanner();
        var forecast = new PlanCalculator(scanner, cache).ForPlan(selection.BuildPlan(t.Dir("Dest"), "Backup"), blocking: true).Sum.Forecast(true);
        var (r, _) = await Copy(selection, t);

        Assert.Equal(forecast.SelectedFileCount, r.SelectedFileCount);
        Assert.Equal(forecast.PlannedCopyFileCount, r.PlannedCopyFileCount);
        Assert.Equal(forecast.PlannedCopyBytes, r.PlannedCopyBytes);
        Assert.Equal(forecast.SkippedCloudOnlyCount, r.SkippedCloudOnlyCount);
        Assert.Equal(10, r.CopiedFileCount);
    }
}
