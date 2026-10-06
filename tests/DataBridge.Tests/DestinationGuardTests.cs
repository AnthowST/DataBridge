using System.Diagnostics;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

/// <summary>V1.1: a backup folder inside a selected source (recursive copy) blocks the launch.</summary>
public class DestinationGuardTests
{
    private static CopyJob Job(string source, string label = "Source", bool isFile = false) => new()
    {
        Category = Categories.Additional,
        Label = label,
        SourcePath = source,
        DestinationRelative = @"Additional\x",
        IsFile = isFile,
    };

    [Fact]
    public async Task Destination_inside_selected_Documents_is_blocked()
    {
        using var t = new TempTree();
        var (selection, _) = CountersTests.SelectionWithDocuments(t);
        t.File(@"Documents\rapport.docx", 100);
        var destination = t.Dir(@"Documents\Sauvegardes");

        var plan = selection.BuildPlan(destination, "DataBridge_PC_tech_2026-10-06_10-00");
        var conflict = DestinationGuard.Check(plan);

        Assert.NotNull(conflict);
        Assert.Equal("Documents", conflict!.SourceLabel);
        Assert.Equal(OverlapKind.BackupInsideSource, conflict.Kind);
        Assert.Equal("Destination invalide", DestinationConflict.Title);
        Assert.Equal("Le dossier de sauvegarde se trouve à l'intérieur d'un dossier sélectionné pour la copie.", DestinationConflict.Message);

        // The engine refuses too (last line of defence): nothing is created, nothing is copied.
        var result = await new CopyEngine(plan, new CopyProgress()).RunAsync(CancellationToken.None);
        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        Assert.Contains("Destination invalide", result.FatalMessage);
        Assert.False(Directory.Exists(plan.BackupRoot));
        Assert.Equal(0, result.CopiedFileCount);
    }

    [Fact]
    public void Destination_in_an_unchecked_subfolder_of_Documents_is_accepted()
    {
        using var t = new TempTree();
        var (selection, docs) = CountersTests.SelectionWithDocuments(t);
        t.File(@"Documents\rapport.docx", 100);
        var destination = t.Dir(@"Documents\Sauvegardes");
        var cache = new DirectoryListingCache();
        docs.Tree!.Set(destination, false, dir => cache.Get(dir).Entries.Select(e => e.FullPath));

        // Documents stays selected (partially) but its Sauvegardes subfolder is not: no recursive copy possible.
        Assert.Null(DestinationGuard.Check(selection.BuildPlan(destination, "Backup")));
    }

    [Fact]
    public void Destination_inside_a_selected_subfolder_of_an_unchecked_Documents_is_blocked()
    {
        using var t = new TempTree();
        var (selection, docs) = CountersTests.SelectionWithDocuments(t);
        var projects = t.Dir(@"Documents\Projets");
        t.File(@"Documents\Projets\plan.pdf", 10);
        docs.Tree!.SetAll(false);
        docs.Tree!.Set(projects, true);

        Assert.NotNull(DestinationGuard.Check(selection.BuildPlan(t.Dir(@"Documents\Projets\Backups"), "Backup")));
        Assert.Null(DestinationGuard.Check(selection.BuildPlan(t.Dir(@"Documents\Autre"), "Backup")));
    }

    [Fact]
    public void Additional_folder_containing_the_destination_is_blocked()
    {
        using var t = new TempTree();
        var (selection, _) = CountersTests.SelectionWithDocuments(t);
        var data = t.Dir("Donnees");
        t.File(@"Donnees\compta.xlsx", 10);
        selection.Additional.Add(new AdditionalItem(data, true));
        var destination = t.Dir(@"Donnees\Backups");
        var plan = selection.BuildPlan(destination, "Backup");

        var status = selection.AnalyzeAdditional(plan.BackupRoot).Single().Value;
        Assert.Equal(AdditionalStatusKind.ContainsDestination, status.Kind);
        Assert.Contains("contient la destination", status.Message);

        var conflict = DestinationGuard.Check(plan);
        Assert.NotNull(conflict);
        Assert.Equal(data, conflict!.SourcePath);
    }

    [Fact]
    public void Destination_folder_itself_added_as_source_is_detected_in_both_directions()
    {
        using var t = new TempTree();
        var backups = t.Dir("Backups");
        var backupRoot = Path.Combine(backups, "DataBridge_PC_tech_2026-10-06_10-00");

        Assert.Equal(OverlapKind.BackupInsideSource, DestinationGuard.Overlap(backups, backupRoot));        // D:\Backups
        Assert.Equal(OverlapKind.BackupInsideSource, DestinationGuard.Overlap(t.Root, backupRoot));        // D:\
        Assert.Equal(OverlapKind.SourceInsideBackup, DestinationGuard.Overlap(Path.Combine(backupRoot, "Personal"), backupRoot));
        Assert.Null(DestinationGuard.Overlap(t.Dir(@"Backups\Ancienne sauvegarde"), backupRoot));          // sibling: no loop
    }

    [Theory]
    [InlineData(@"C:\Users\tech\Documents", @"c:\USERS\tech\documents\Sauvegardes\DataBridge_X")]               // case
    [InlineData(@"C:\Users\tech\Documents", @"C:\Users\tech\Images\..\Documents\Backups\DataBridge_X")]       // ".."
    [InlineData(@"C:\Users\tech\Documents\", @"C:/Users/tech/Documents/DataBridge_X")]                       // separators
    [InlineData(@"C:\Users\tech\Documents", @"\\?\C:\Users\tech\Documents\DataBridge_X")]                     // extended prefix
    [InlineData(@"\\NAS\Partage\Donnees", @"\\nas\partage\DONNEES\Backups\DataBridge_X")]                     // UNC, case
    [InlineData(@"\\NAS\Partage\Donnees", @"\\?\UNC\NAS\Partage\Donnees\DataBridge_X")]                       // UNC extended
    public void Overlaps_are_detected_whatever_the_spelling(string source, string backupRoot) =>
        Assert.NotNull(DestinationGuard.Check([Job(source)], backupRoot));

    [Theory]
    [InlineData(@"C:\Users\tech\Documents", @"\\NAS\Backups\DataBridge_X")]                      // network destination, local source
    [InlineData(@"C:\Users\tech\Documents", @"\\NAS\Users\tech\Documents\DataBridge_X")]         // same tail on another root
    [InlineData(@"\\NAS\Partage", @"\\NAS\Partage2\DataBridge_X")]                              // share name prefix
    [InlineData(@"\\NAS\Partage\Donnees", @"\\NAS2\Partage\Donnees\DataBridge_X")]              // other server
    [InlineData(@"C:\Users\tech\Documents", @"C:\Users\tech\Documents2\DataBridge_X")]           // folder name prefix
    [InlineData(@"C:\Users\tech\Documents", @"D:\Users\tech\Documents\DataBridge_X")]            // other drive
    public void Network_and_unc_destinations_produce_no_false_positive(string source, string backupRoot) =>
        Assert.Null(DestinationGuard.Check([Job(source)], backupRoot));

    [Fact]
    public void Mapped_network_drive_and_its_unc_path_are_the_same_location()
    {
        var previous = PathIdentity.NetworkDriveResolver;
        PathIdentity.NetworkDriveResolver = letter => letter == 'Q' ? @"\\nas\partage" : null;
        try
        {
            Assert.NotNull(DestinationGuard.Check([Job(@"\\NAS\Partage\Donnees")], @"Q:\Donnees\Backups\DataBridge_X"));
            Assert.NotNull(DestinationGuard.Check([Job(@"Q:\Donnees")], @"\\nas\partage\donnees\DataBridge_X"));
            Assert.Null(DestinationGuard.Check([Job(@"\\NAS\Partage\Autre")], @"Q:\Donnees\DataBridge_X"));
            Assert.Null(DestinationGuard.Check([Job(@"C:\Users\tech\Documents")], @"Q:\Users\tech\Documents\DataBridge_X"));
        }
        finally
        {
            PathIdentity.NetworkDriveResolver = previous;
        }
    }

    [Fact]
    public void Destination_reached_through_a_junction_is_resolved()
    {
        using var t = new TempTree();
        var docs = t.Dir("Documents");
        var link = t.P("RaccourciDocs");
        // Directory junction: created without administrator rights.
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{docs}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link));

        Assert.NotNull(DestinationGuard.Check([Job(docs, "Documents")], Path.Combine(link, "DataBridge_X")));
        Directory.Delete(link); // removes the junction only
    }
}
