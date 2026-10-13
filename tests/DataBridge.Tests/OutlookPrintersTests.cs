using System.Text.Json;
using DataBridge.Core.Copy;
using DataBridge.Core.Outlook;
using DataBridge.Core.PrinterTransfer;
using DataBridge.Core.Restore;
using DataBridge.Core.Selection;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Model;

namespace DataBridge.Tests;

/// <summary>
/// 2.1: Outlook mailbox and printers in the backup and the restore. Nothing here talks to the real Outlook or to the
/// real printers of the PC: the engines' results are built by hand, only DataBridge's own logic is exercised.
/// </summary>
public class OutlookPrintersTests
{
    private static OperationResult OutlookDetail(string pst, string bin)
    {
        var d = new OperationResult { Kind = OperationKind.Backup, PstPath = pst, AutocompleteRequested = true, AutocompleteOk = true };
        d.Mails.Requested = d.Mails.Saved = 12;
        d.Contacts.Requested = d.Contacts.Saved = 3;
        d.Events.Requested = d.Events.Saved = 5;
        d.AutocompleteFiles.Add(bin);
        return d;
    }

    /// <summary>A real 2.x backup (copy engine), completed with a printers archive and a mailbox as the 2.1 engine writes them.</summary>
    private static string BackupWithOutlookAndPrinters(TempTree t, OutlookCategories categories = OutlookCategories.All)
    {
        var root = RestoreTests.RealBackup(t);
        var plan = new CopyPlan
        {
            Profile = RestoreTests.Profile(t.Dir(@"Users\Old"), "Old"),
            DestinationBase = Path.GetDirectoryName(root)!,
            BackupFolderName = Path.GetFileName(root),
            Jobs = [],
            Outlook = new OutlookBackupJob("store-1", "anthony@exemple.ch", categories),
            Printers = new PrinterBackupJob(new LiveInventory(), ["P1"]),
        };
        var archive = t.File(Path.Combine(root, PrinterBackupJob.DestinationRelative), 2048);
        var pst = t.File(Path.Combine(root, @"Outlook\Boite\Sauvegarde_Outlook_2026-10-08_10-00-00.pst"), 4096);
        var bin = t.File(Path.Combine(root, @"Outlook\Boite\Autocompletion_2026-10-08_10-00-00.bin"), 64);
        var result = new CopyResult { BackupPath = root, StartedAt = DateTime.Now, Outcome = CopyOutcome.Completed };
        result.Printers = new PrinterBackupResult { ArchivePath = archive, ArchiveSize = 2048 };
        result.Printers.Printers.Add(new PrinterEntry { Id = "P1", Name = "Brother Bureau", Kind = PrinterKind.TcpIp, Support = SupportLevel.Supported, IsDefault = true });
        result.Outlook = new OutlookTransferResult { Categories = categories, MailboxName = "anthony@exemple.ch", Detail = OutlookDetail(pst, bin) };
        BackupManifestWriter.Write(root, BackupManifestWriter.Build(plan, result, DateTime.Now));
        return root;
    }

    [Fact]
    public void Manifest_describes_the_printers_archive_and_the_mailbox_without_content()
    {
        using var t = new TempTree();
        var root = BackupWithOutlookAndPrinters(t);
        var json = File.ReadAllText(Path.Combine(root, BackupManifest.FileName));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal(1, r.GetProperty("schemaVersion").GetInt32()); // additive sections: older 2.0 readers ignore them
        Assert.Equal(@"Imprimantes\Imprimantes.prnbak", r.GetProperty("printers").GetProperty("file").GetString());
        Assert.Equal("Brother Bureau", r.GetProperty("printers").GetProperty("printers")[0].GetProperty("name").GetString());
        var mb = r.GetProperty("outlookMailbox");
        Assert.Equal(@"Outlook\Boite\Sauvegarde_Outlook_2026-10-08_10-00-00.pst", mb.GetProperty("pstFile").GetString());
        Assert.Equal(12, mb.GetProperty("mails").GetInt32());
        Assert.Equal("completed", mb.GetProperty("status").GetString());
        Assert.Equal(["mails", "calendar", "contacts", "autocomplete"], mb.GetProperty("categories").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Contains("printers", r.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("outlookMailbox", r.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        foreach (var forbidden in new[] { "password", "keyMaterial", "token", "cookie", "secret" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_reads_the_mailbox_and_needs_an_explicit_target()
    {
        using var t = new TempTree();
        var root = BackupWithOutlookAndPrinters(t);
        var content = BackupReader.Open(root).Content!;
        Assert.NotNull(content.Printers);
        Assert.Equal("Brother Bureau", content.Printers!.Printers.Single().Name);
        Assert.NotNull(content.Mailbox);
        Assert.Equal(OutlookCategories.All, content.Mailbox!.Available);
        Assert.Contains("Boîte Outlook", content.CategoryLabels());

        var selection = RestoreSelection.Create(content, RestoreTests.Target(t));
        Assert.Equal(OutlookCategories.All, selection.Mailbox!.Selected);
        // The target mailbox is never guessed: blocking until it is chosen.
        var plan = RestorePlanner.BuildPlan(selection);
        Assert.Null(plan.Outlook);
        Assert.Contains(RestorePlanner.Validate(selection, plan), i => i.IsBlocking && i.Message.Contains("choisissez la boîte de destination"));

        selection.Mailbox.TargetStoreId = "target-store";
        selection.Mailbox.TargetName = "nouveau@exemple.ch";
        selection.Mailbox.Selected &= ~OutlookCategories.Calendar;
        plan = RestorePlanner.BuildPlan(selection);
        Assert.NotNull(plan.Outlook);
        Assert.Equal(OutlookCategories.Mails | OutlookCategories.Contacts | OutlookCategories.Autocomplete, plan.Outlook!.Categories);
        Assert.Equal(content.Mailbox.PstFullPath, plan.Outlook.PstPath);
        Assert.False(plan.IsEmpty);
        Assert.DoesNotContain(RestorePlanner.Validate(selection, plan), i => i.IsBlocking && i.Message.Contains("choisissez la boîte de destination"));

        selection.Mailbox.Selected = OutlookCategories.None;
        plan = RestorePlanner.BuildPlan(selection);
        Assert.Null(plan.Outlook);
        Assert.Contains("Boîte Outlook : non sélectionnée", plan.NotSelected);
    }

    [Fact]
    public void Unchecking_the_mailbox_main_switch_restores_nothing_and_keeps_the_categories()
    {
        using var t = new TempTree();
        var content = BackupReader.Open(BackupWithOutlookAndPrinters(t)).Content!;
        var selection = RestoreSelection.Create(content, RestoreTests.Target(t));
        Assert.True(selection.Mailbox!.IncludeMailbox);

        // Unchecked without a target: no job, and no blocking "choose the target mailbox".
        selection.Mailbox.IncludeMailbox = false;
        var plan = RestorePlanner.BuildPlan(selection);
        Assert.Null(plan.Outlook);
        Assert.Contains("Boîte Outlook : non sélectionnée", plan.NotSelected);
        Assert.DoesNotContain(RestorePlanner.Validate(selection, plan), i => i.Message.Contains("Boîte Outlook"));

        // Unchecked with a target and categories: still nothing restored.
        selection.Mailbox.TargetStoreId = "target-store";
        selection.Mailbox.TargetName = "nouveau@exemple.ch";
        Assert.Null(RestorePlanner.BuildPlan(selection).Outlook);

        // Checked again: the categories chosen before are kept.
        selection.Mailbox.Selected = OutlookCategories.Mails | OutlookCategories.Contacts;
        selection.Mailbox.IncludeMailbox = true;
        Assert.Equal(OutlookCategories.Mails | OutlookCategories.Contacts, RestorePlanner.BuildPlan(selection).Outlook!.Categories);
    }

    [Fact]
    public void Manifest_paths_of_the_mailbox_cannot_leave_the_backup()
    {
        using var t = new TempTree();
        var root = BackupWithOutlookAndPrinters(t);
        var path = Path.Combine(root, BackupManifest.FileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), BackupManifest.JsonOptions)!;
        manifest.OutlookMailbox!.PstFile = @"..\..\Users\New\evil.pst";
        manifest.OutlookMailbox.AutocompleteFile = null;
        manifest.Printers!.File = @"C:\Windows\System32\spool\x.prnbak";
        BackupManifestWriter.Write(root, manifest);

        var content = BackupReader.Open(root).Content!;
        Assert.Null(content.Mailbox);
        Assert.Null(content.Printers);
        Assert.Equal(2, content.SecurityIssues.Count);
    }

    [Fact]
    public void Backup_export_takes_everything_of_the_checked_categories()
    {
        var content = new StoreContent();
        var inbox = new FolderNode { EntryId = "inbox", Name = "Boîte de réception", Path = ["Boîte de réception"], PathSpecial = [6], SpecialType = 6 };
        inbox.Mails.Add(new MailEntry { EntryId = "m1", Subject = "a", Folder = inbox });
        inbox.Mails.Add(new MailEntry { EntryId = "m2", Subject = "b", Folder = inbox });
        var archive = new FolderNode { EntryId = "arch", Name = "Archives", Path = ["Boîte de réception", "Archives"], PathSpecial = [6, 0], Parent = inbox };
        inbox.Children.Add(archive);
        content.MailRoots.Add(inbox);
        content.Contacts.Add(new ContactEntry { EntryId = "c1", Name = "Jean" });
        content.Events.Add(new EventEntry { EntryId = "e1", Subject = "Réunion" });
        content.Autocomplete = new AutocompleteInfo { Found = false, Message = "aucun cache" };

        var job = new OutlookBackupJob("s", "Boîte", OutlookCategories.Mails | OutlookCategories.Contacts | OutlookCategories.Autocomplete);
        var request = OutlookTransfer.BuildExport(job, content, @"E:\Sauvegarde\Outlook\Boite");
        Assert.Equal(2, request.MailJobs.Count);                    // empty folders are recreated too
        Assert.Equal(2, request.MailJobs.Sum(j => j.Mails.Count));   // every mail, no choice item by item
        Assert.Single(request.Contacts);
        Assert.Empty(request.Events);                                // calendar not checked
        Assert.False(request.IncludeAutocomplete);                   // no cache in this mailbox
        Assert.True(request.DetachPst);                              // the PST does not stay in the Outlook profile
        Assert.Equal(@"E:\Sauvegarde\Outlook\Boite", request.DestDir);
    }

    [Fact]
    public void Restore_request_only_adds_and_skips_duplicates()
    {
        var job = new OutlookRestoreJob(null, @"E:\Sauvegarde\Outlook\Boite\Autocompletion.bin", "target", "nouveau@exemple.ch",
            OutlookCategories.Autocomplete, IntoNewFolder: false, ReplaceAutocompleteCache: false);
        var request = OutlookTransfer.BuildRestore(job, backup: null, content: null, @"C:\Temp\Rapport");
        Assert.Null(request.BackupStoreId);   // autocomplete only: no PST to open
        Assert.True(request.SkipDuplicates);
        Assert.True(request.AutoAsContacts);
        Assert.False(request.AutoReplace);    // the cache is replaced only on explicit request
        Assert.Empty(request.MailJobs);
        Assert.False(request.BackupOpenedDirectly);
    }

    [Fact]
    public void Backup_plan_carries_the_mailbox_and_the_checked_printers()
    {
        using var t = new TempTree();
        var selection = new BackupSelection { Profile = RestoreTests.Profile(t.Dir(@"Users\Old"), "Old") };
        var inventory = new LiveInventory();
        inventory.Printers.Add(new LivePrinter { Entry = new PrinterEntry { Id = "P1", Name = "Brother Bureau" } });
        inventory.Printers.Add(new LivePrinter { Entry = new PrinterEntry { Id = "P2", Name = "Microsoft Print to PDF" } });
        selection.PrinterInventory = inventory;
        selection.SelectedPrinters.Add("P1");
        selection.OutlookMailbox = new OutlookBackupJob("s", "Boîte", OutlookCategories.None);

        var plan = selection.BuildPlan(t.Dir("Dest"), "Backup");
        Assert.Null(plan.Outlook);                                   // nothing checked: no PST
        Assert.Equal(["P1"], plan.Printers!.PrinterIds);
        Assert.Contains("Imprimante Microsoft Print to PDF : non sélectionnée", plan.NotSelected);

        selection.OutlookMailbox = new OutlookBackupJob("s", "Boîte", OutlookCategories.Mails);
        Assert.Equal(OutlookCategories.Mails, selection.BuildPlan(t.Dir("Dest"), "Backup").Outlook!.Categories);
    }

    [Fact]
    public void Outlook_categories_round_trip_through_their_manifest_names()
    {
        foreach (var c in new[] { OutlookCategories.None, OutlookCategories.Mails, OutlookCategories.Calendar | OutlookCategories.Autocomplete, OutlookCategories.All })
            Assert.Equal(c, OutlookCategoryText.FromNames(OutlookCategoryText.ToNames(c)));
        Assert.Equal("mails, contacts", OutlookCategoryText.Describe(OutlookCategories.Mails | OutlookCategories.Contacts));
    }

    [Fact]
    public void Elevated_printer_worker_is_a_dedicated_switch_of_DataBridge()
    {
        Assert.Equal("--printer-worker", DataBridge.Printers.Elevation.WorkerHost.Argument);
    }
}
