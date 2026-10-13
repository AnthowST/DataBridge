using System.Text.Json;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Restore;

namespace DataBridge.Tests;

/// <summary>
/// 2.2: network drives in the backup and the restore. Nothing here connects a real drive: the backup goes through the
/// real copy engine with drives built by hand, the restore stops at the plan and its checks.
/// </summary>
public class NetworkDriveTests
{
    private static readonly NetworkDriveInfo Commun = new("S:", @"\\srv-fichiers\Commun", null, Persistent: true, Connected: true);
    private static readonly NetworkDriveInfo Compta = new("T:", @"\\srv-fichiers\Compta", @"ENTREPRISE\compta", Persistent: true, Connected: false);

    private static string BackupWithDrives(TempTree t, params NetworkDriveInfo[] selected) => RestoreTests.RealBackup(t, select: s =>
    {
        s.NetworkDrives.AddRange([Commun, Compta]);
        foreach (var d in selected) s.SelectedNetworkDrives.Add(d.Letter);
    });

    [Fact]
    public void Selected_drives_are_written_in_the_manifest_and_the_log_without_password()
    {
        using var t = new TempTree();
        var root = BackupWithDrives(t, Commun, Compta);
        var json = File.ReadAllText(Path.Combine(root, BackupManifest.FileName));
        using var doc = JsonDocument.Parse(json);
        var drives = doc.RootElement.GetProperty("networkDrives");
        Assert.Equal(2, drives.GetArrayLength());
        Assert.Equal("T:", drives[1].GetProperty("letter").GetString());
        Assert.Equal(@"\\srv-fichiers\Compta", drives[1].GetProperty("remotePath").GetString());
        Assert.Equal(@"ENTREPRISE\compta", drives[1].GetProperty("userName").GetString());
        Assert.False(drives[0].TryGetProperty("userName", out _)); // session credentials: no account written
        Assert.Contains("networkDrives", doc.RootElement.GetProperty("categories").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        var log = File.ReadAllText(Path.Combine(root, "copy-log.txt"));
        Assert.Contains(@"T: → \\srv-fichiers\Compta — compte : ENTREPRISE\compta", log);
        Assert.Contains(@"S: → \\srv-fichiers\Commun — compte : Identifiants de la session Windows", log);
    }

    [Fact]
    public void Unselected_drive_is_not_saved()
    {
        using var t = new TempTree();
        var root = BackupWithDrives(t, Compta);
        var content = BackupReader.Open(root).Content!;
        Assert.Equal(["T:"], content.NetworkDrives.Select(d => d.Letter));
        Assert.Contains(@"Lecteur réseau S: (\\srv-fichiers\Commun) : non sélectionné", File.ReadAllText(Path.Combine(root, "copy-log.txt")));
    }

    [Fact]
    public void Restore_reads_the_drives_and_plans_the_selected_ones()
    {
        using var t = new TempTree();
        var content = BackupReader.Open(BackupWithDrives(t, Commun, Compta)).Content!;
        Assert.Equal(2, content.NetworkDrives.Count);
        Assert.Equal(@"ENTREPRISE\compta", content.NetworkDrives.Single(d => d.Letter == "T:").UserName);
        Assert.Contains("2 lecteurs réseau", content.CategoryLabels());

        var selection = RestoreSelection.Create(content, RestoreTests.Target(t));
        Assert.True(selection.AskNetworkPassword);
        Assert.Equal(2, RestorePlanner.BuildPlan(selection).NetworkDrives.Count);

        selection.SelectedNetworkDrives.Remove("S:");
        selection.AskNetworkPassword = false;
        var plan = RestorePlanner.BuildPlan(selection);
        Assert.Equal(["T:"], plan.NetworkDrives.Select(d => d.Letter));
        Assert.False(plan.AskNetworkPassword);
        Assert.Contains(@"Lecteur réseau S: (\\srv-fichiers\Commun) : non sélectionné", plan.NotSelected);
    }

    [Fact]
    public void A_backup_with_only_network_drives_is_restorable()
    {
        using var t = new TempTree();
        var content = new BackupContent
        {
            Root = t.Root,
            Generation = BackupGeneration.Manifest,
            Source = new BackupSourceInfo(null, null, null, null, null, null, null, null),
            NetworkDrives = [new BackupNetworkDrive("S:", @"\\srv\Commun", null, true)],
        };
        Assert.False(content.IsEmpty);
        var selection = RestoreSelection.Create(content, RestoreTests.Target(t));
        var plan = RestorePlanner.BuildPlan(selection);
        Assert.False(plan.IsEmpty);
        Assert.DoesNotContain(RestorePlanner.Validate(selection, plan), i => i.IsBlocking);
    }

    [Fact]
    public void Invalid_drives_in_a_manifest_are_ignored()
    {
        using var t = new TempTree();
        var root = BackupWithDrives(t, Commun);
        var path = Path.Combine(root, BackupManifest.FileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(path), BackupManifest.JsonOptions)!;
        manifest.NetworkDrives.Add(new ManifestNetworkDrive { Letter = "ZZ", RemotePath = @"\\srv\x" });
        manifest.NetworkDrives.Add(new ManifestNetworkDrive { Letter = "Y:", RemotePath = @"C:\Windows" });
        manifest.NetworkDrives.Add(new ManifestNetworkDrive { Letter = "s:", RemotePath = @"\\autre\doublon" });
        BackupManifestWriter.Write(root, manifest);

        var content = BackupReader.Open(root).Content!;
        Assert.Equal(["S:"], content.NetworkDrives.Select(d => d.Letter));
        Assert.Equal(2, content.Warnings.Count(w => w.StartsWith("Lecteur réseau ignoré", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_letter_already_used_on_this_pc_is_never_taken_over()
    {
        var env = new NetworkDriveEnvironment(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["C:"] = null,
            ["S:"] = @"\\SRV-FICHIERS\commun\",
            ["T:"] = @"\\autre-serveur\Partage",
        });
        Assert.True(env.IsAlreadyMapped("S:", @"\\srv-fichiers\Commun"));
        Assert.Null(env.Conflict("S:", @"\\srv-fichiers\Commun"));
        Assert.Contains(@"déjà connectée à \\autre-serveur\Partage", env.Conflict("T:", @"\\srv-fichiers\Compta"));
        Assert.Contains("disque de ce PC", env.Conflict("C:", @"\\srv\x"));
        Assert.Null(env.Conflict("U:", @"\\srv\x"));

        // The engine refuses a conflicting letter before calling Windows.
        var r = NetworkDriveRestoreService.Connect(new BackupNetworkDrive("T:", @"\\srv-fichiers\Compta", null, true), env, IntPtr.Zero, askPassword: false);
        Assert.Equal(NetworkDriveRestoreOutcome.Conflict, r.Outcome);
        var same = NetworkDriveRestoreService.Connect(new BackupNetworkDrive("S:", @"\\srv-fichiers\Commun", null, true), env, IntPtr.Zero, askPassword: false);
        Assert.Equal(NetworkDriveRestoreOutcome.AlreadyPresent, same.Outcome);
    }

    [Theory]
    [InlineData(@"\\srv\partage", true)]
    [InlineData(@"\\srv.domaine.local\partage\sous dossier", true)]
    [InlineData(@"\\srv", false)]
    [InlineData(@"\\\srv\partage", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData("", false)]
    public void Only_unc_shares_are_accepted(string path, bool valid) => Assert.Equal(valid, NetworkDriveService.IsValidRemotePath(path));

    [Fact]
    public void Detection_of_this_session_does_not_fail()
    {
        // Read-only: HKCU\Network and the connected drives of the session running the tests.
        var drives = NetworkDriveService.Detect();
        Assert.All(drives, d => Assert.True(NetworkDriveService.IsValidLetter(d.Letter)));
    }
}
