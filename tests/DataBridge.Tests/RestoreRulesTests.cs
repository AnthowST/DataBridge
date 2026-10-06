using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Restore;

namespace DataBridge.Tests;

/// <summary>2.0 restore: small rules (conflict decision, browser target choice, Firefox registration, Wi-Fi reading).</summary>
public class RestoreRulesTests
{
    private static FsEntry Existing(long size, DateTime writeUtc) => new()
    {
        Name = "f", FullPath = @"C:\x\f", RawAttributes = (uint)FileAttributes.Archive, Size = size, ReparseTag = 0, LastWriteUtc = writeUtc,
    };

    private static readonly DateTime T = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ConflictPolicy.Replace, 100, 0, FileDecision.Identical)]
    [InlineData(ConflictPolicy.Replace, 100, 1, FileDecision.Identical)]        // within the 2 s tolerance (FAT, shares)
    [InlineData(ConflictPolicy.Replace, 90, -3600, FileDecision.Replace)]
    [InlineData(ConflictPolicy.Keep, 90, -3600, FileDecision.Keep)]
    [InlineData(ConflictPolicy.ReplaceIfNewer, 90, -3600, FileDecision.Replace)] // destination older than the backup
    [InlineData(ConflictPolicy.ReplaceIfNewer, 90, 3600, FileDecision.Keep)]     // destination newer: kept
    [InlineData(ConflictPolicy.ReplaceIfNewer, 90, 0, FileDecision.Keep)]        // same date, other size: never replaced blindly
    public void Conflict_decision_uses_size_and_date_only(ConflictPolicy policy, long existingSize, int existingOffsetSeconds, FileDecision expected)
    {
        var decision = ConflictResolver.Decide(policy, 100, T, Existing(existingSize, T.AddSeconds(existingOffsetSeconds)));
        Assert.Equal(expected, decision);
        Assert.Equal(FileDecision.Restore, ConflictResolver.Decide(policy, 100, T, null));
    }

    private static BrowserTargetOption Opt(string key, bool creates = false, bool active = false) =>
        new(key, key, "", @"C:\p\" + key, creates, active);

    [Fact]
    public void Browser_target_is_chosen_automatically_only_when_unambiguous()
    {
        Assert.Equal("existing:A", BrowserRestoreService.DefaultOption(BrowserKind.Chrome, [Opt("existing:A")])!.Key);
        Assert.Equal("new", BrowserRestoreService.DefaultOption(BrowserKind.Chrome, [Opt("new", creates: true)])!.Key);
        Assert.Null(BrowserRestoreService.DefaultOption(BrowserKind.Chrome, [Opt("existing:A", active: true), Opt("existing:B")]));
        // Firefox: one active profile among several may be proposed automatically.
        Assert.Equal("existing:A", BrowserRestoreService.DefaultOption(BrowserKind.Firefox,
            [Opt("existing:A", active: true), Opt("existing:B"), Opt("new", creates: true)])!.Key);
        Assert.Null(BrowserRestoreService.DefaultOption(BrowserKind.Firefox, [Opt("existing:A"), Opt("existing:B"), Opt("new", creates: true)]));
    }

    [Fact]
    public void Firefox_profile_is_appended_to_an_existing_profiles_ini_with_a_safety_copy()
    {
        using var t = new TempTree();
        var root = t.Dir("Firefox");
        var ini = Path.Combine(root, "profiles.ini");
        File.WriteAllText(ini, "[Install308046B0AF4A39CB]\nDefault=Profiles/abc.default-release\nLocked=1\n\n[Profile0]\nName=default-release\nIsRelative=1\nPath=Profiles/abc.default-release\n");
        var message = BrowserRestoreService.RegisterFirefoxProfile(
            new FirefoxRegistration(root, "default-release (DataBridge)", @"Profiles\xyz.default-release-databridge", MakeDefault: false), t.P("Safety"));

        var text = File.ReadAllText(ini);
        Assert.Contains("[Profile1]", text);
        Assert.Contains("Path=Profiles/xyz.default-release-databridge", text);
        Assert.DoesNotContain("Default=1", text);                       // the existing installation keeps its default profile
        Assert.Contains("Locked=1", text);
        Assert.True(File.Exists(t.P(@"Safety\profiles.ini")));
        Assert.Contains("about:profiles", message);

        var resolved = FirefoxProfiles.Resolve(root, text, null, _ => true);
        Assert.Equal(2, resolved.Profiles.Count);
        Assert.Equal(Path.Combine(root, @"Profiles\abc.default-release"), resolved.ActiveProfilePaths.Single());
    }

    [Fact]
    public void Wifi_profile_file_is_read_without_exposing_the_key()
    {
        using var t = new TempTree();
        var path = t.P("Wi-Fi-Bureau.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>Bureau-Entreprise</name>" +
            "<MSM><security><authEncryption><authentication>WPA2PSK</authentication></authEncryption><sharedKey><keyType>passPhrase</keyType>" +
            "<protected>false</protected><keyMaterial>TOP-SECRET</keyMaterial></sharedKey></security></MSM></WLANProfile>");
        var info = WifiRestoreService.ReadProfileFile(path);
        Assert.NotNull(info);
        Assert.Equal("Bureau-Entreprise", info.Value.Name);
        Assert.Equal("WPA2-Personnel", info.Value.Authentication);
        Assert.True(info.Value.KeyIncluded);
        Assert.DoesNotContain("TOP-SECRET", info.Value.ToString());

        File.WriteAllText(t.P("pas-wifi.xml"), "<root><name>x</name></root>");
        Assert.Null(WifiRestoreService.ReadProfileFile(t.P("pas-wifi.xml")));
    }

    [Theory]
    [InlineData(@"D:\Comptabilité", true)]
    [InlineData(@"\\serveur\partage\Compta", true)]
    [InlineData(@"Comptabilité", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    [InlineData(@"C:\dossier\fichier.txt:flux", false)]
    [InlineData(@"C:\dossier\..\..\Windows", false)] // ".." is refused, even inside an absolute path
    public void Absolute_destinations_from_a_backup_are_normalized_or_refused(string path, bool accepted)
    {
        var normalized = RestorePaths.NormalizeAbsolute(path);
        Assert.Equal(accepted, normalized != null);
        Assert.True(RestorePaths.IsProtectedLocation(@"C:\Windows\System32"));
        Assert.False(RestorePaths.IsProtectedLocation(@"D:\Comptabilité"));
    }
}
