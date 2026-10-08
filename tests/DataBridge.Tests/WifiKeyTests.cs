using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Profile;
using DataBridge.Core.Restore;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

/// <summary>
/// 2.1.1: Wi-Fi keys refused to the session, recovered through the elevated worker. No UAC prompt here: the worker
/// side runs in-process on the same pipe protocol, and the backup tests only use profiles whose XML is already known.
/// </summary>
public class WifiKeyTests
{
    private const string Key = "TEST-KEY-456";

    private static string ProfileXml(string name, bool clearKey) => $"""
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
        	<name>{name}</name>
        	<SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig>
        	<connectionType>ESS</connectionType>
        	<connectionMode>auto</connectionMode>
        	<MSM><security>
        		<authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
        		<sharedKey><keyType>passPhrase</keyType><protected>{(clearKey ? "false" : "true")}</protected><keyMaterial>{(clearKey ? Key : "01000000D08C9DDF")}</keyMaterial></sharedKey>
        	</security></MSM>
        </WLANProfile>
        """;

    private static WifiProfileInfo Profile(string name) =>
        new(name, Guid.Empty, "Carte de test", false, false, "WPA2-Personnel", WifiKeyStatus.NotAccessible);

    [Fact]
    public void Recovered_xml_is_written_as_is_and_marked_as_admin_key()
    {
        using var t = new TempTree();
        var folder = t.Dir("WiFi");
        var xml = ProfileXml("Bureau", clearKey: true);

        var r = WifiService.Export(Profile("Bureau"), folder, xml);

        Assert.True(r.Exported, r.Error);
        Assert.True(r.KeyIncluded);
        Assert.True(r.KeyViaAdmin);
        Assert.Equal(xml, File.ReadAllText(Path.Combine(folder, r.FileName!)));
        Assert.Equal("Clé récupérée avec les droits administrateur",
            (Profile("Bureau") with { KeyStatus = WifiKeyStatus.Available, KeyViaAdmin = true }).KeyLabel);
    }

    [Fact]
    public void Recovered_xml_without_clear_key_is_not_reported_as_admin_key()
    {
        using var t = new TempTree();
        var r = WifiService.Export(Profile("Bureau"), t.Dir("WiFi"), ProfileXml("Bureau", clearKey: false));

        Assert.True(r.Exported, r.Error);
        Assert.False(r.KeyIncluded);
        Assert.False(r.KeyViaAdmin);
    }

    [Fact]
    public void Worker_reports_an_unknown_profile_as_still_protected()
    {
        var unknown = Profile("DataBridge-test-" + Guid.NewGuid().ToString("N"));

        var recovery = WifiKeyElevation.Exchange([unknown],
            pipe => Task.Run(() => WifiKeyElevation.RunWorker(pipe, Environment.ProcessId)), TimeSpan.FromSeconds(30));

        Assert.Empty(recovery.Recovered);
        Assert.Equal([unknown.Name], recovery.StillProtected);
        Assert.False(string.IsNullOrEmpty(recovery.Account));
    }

    [Fact]
    public void Worker_refuses_a_pipe_that_its_parent_does_not_own()
    {
        // PID 0 never owns a pipe: the worker hangs up without reading anything.
        Assert.Throws<InvalidOperationException>(() => WifiKeyElevation.Exchange([Profile("Bureau")],
            pipe => Task.Run(() => Assert.Equal(2, WifiKeyElevation.RunWorker(pipe, 0))), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Backup_writes_recovered_keys_and_never_logs_them()
    {
        using var t = new TempTree();
        var selection = new BackupSelection
        {
            Profile = new ProfileInfo("PC-TEST", "tech", "PC-TEST", t.Root, t.Root, t.Root, null, "Windows test", ElevationState.Standard),
        };
        selection.WifiProfiles.Add(Profile("Bureau") with { KeyStatus = WifiKeyStatus.Available, KeyViaAdmin = true });
        selection.WifiProfiles.Add(Profile("Atelier") with { KeyStatus = WifiKeyStatus.Available, KeyViaAdmin = true });
        selection.SelectedWifi.Add("Bureau");
        selection.RecoveredWifiXml["Bureau"] = ProfileXml("Bureau", clearKey: true);
        selection.RecoveredWifiXml["Atelier"] = ProfileXml("Atelier", clearKey: true);

        using var target = new TempTree();
        var plan = selection.BuildPlan(target.Dir("Dest"), "Backup");
        // Only the selected profile's XML goes into the plan.
        Assert.Equal(["Bureau"], plan.Wifi!.RecoveredXml!.Keys);

        var result = await new CopyEngine(plan, new CopyProgress()).RunAsync(CancellationToken.None);

        var backup = Path.Combine(target.Root, "Dest", "Backup");
        Assert.Equal(1, result.WifiKeysViaAdmin);
        Assert.Contains(Key, File.ReadAllText(Path.Combine(backup, @"Network\WiFi\Wi-Fi-Bureau.xml")));
        var log = File.ReadAllText(Path.Combine(backup, "copy-log.txt"));
        Assert.Contains("clés incluses : 1 dont 1 avec les droits administrateur", log);
        Assert.Contains("Bureau : profil exporté (Wi-Fi-Bureau.xml) — clé incluse (récupérée avec les droits administrateur)", log);
        Assert.DoesNotContain(Key, log);
        Assert.DoesNotContain(Key, File.ReadAllText(Path.Combine(backup, "databridge-manifest.json")));

        var open = BackupReader.Open(backup);
        Assert.True(open.Success, open.Error);
        Assert.True(open.Content!.Wifi.Single().KeyIncluded);
    }
}
