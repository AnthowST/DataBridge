using DataBridge.Core.Copy;
using DataBridge.Core.Profile;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

/// <summary>1.3.1: which OneDrive account a Known Folder belongs to (pure mapping logic, no registry access).</summary>
public class OneDriveAccountTests
{
    private static OneDriveAccountKey Key(string name, string? folder, string? email = null, string? displayName = null,
        bool? business = null, params string[] libraries) =>
        new(name, folder, email, displayName, null, business, libraries);

    private static OneDriveDetection Build(params OneDriveAccountKey[] keys) => OneDriveAccountService.Build(keys, []);

    private static OneDriveAccount? AccountOf(string path, OneDriveDetection d) => OneDriveAccountService.Match(path, d.Roots)?.Account;

    [Fact]
    public void Case1_personal_account_is_associated()
    {
        var d = Build(Key("Personal", @"C:\Users\Test\OneDrive", "test@outlook.com"));
        var a = AccountOf(@"C:\Users\Test\OneDrive\Desktop", d);
        Assert.NotNull(a);
        Assert.Equal(OneDriveAccountType.Personal, a!.AccountType);
        Assert.Equal("OneDrive · Personnel", a.Title);
        Assert.Equal("test@outlook.com", a.Detail);
    }

    [Fact]
    public void Case2_business_account_is_associated()
    {
        var d = Build(Key("Business1", @"C:\Users\Test\OneDrive - Société", "test@societe.ch", "Société"));
        var a = AccountOf(@"C:\Users\Test\OneDrive - Société\Documents", d);
        Assert.NotNull(a);
        Assert.Equal(OneDriveAccountType.Business, a!.AccountType);
        Assert.Equal("OneDrive · Société", a.Title);
        Assert.Equal("test@societe.ch", a.Detail);
    }

    [Fact]
    public void Case3_the_right_account_is_chosen_among_several()
    {
        var d = Build(
            Key("Personal", @"C:\Users\Test\OneDrive", "test@outlook.com"),
            Key("Business1", @"C:\Users\Test\OneDrive - itelium", "test@itelium.ch", "itelium"),
            Key("Business2", @"C:\Users\Test\OneDrive - Client SA", "test@client.ch", "Client SA"));

        Assert.Equal("Business1", AccountOf(@"C:\Users\Test\OneDrive - itelium\Desktop", d)!.AccountId);
        Assert.Equal("Business2", AccountOf(@"C:\Users\Test\OneDrive - Client SA\Documents", d)!.AccountId);
        Assert.Equal("Personal", AccountOf(@"C:\Users\Test\OneDrive\Images", d)!.AccountId);
    }

    [Fact]
    public void Case4_similar_roots_use_the_most_specific_one()
    {
        // "OneDrive" is a textual prefix of "OneDrive - Company" but not a parent folder of it.
        var d = Build(
            Key("Personal", @"C:\Users\Test\OneDrive"),
            Key("Business1", @"C:\Users\Test\OneDrive - Company", displayName: "Company"));
        Assert.Equal("Business1", AccountOf(@"C:\Users\Test\OneDrive - Company\Desktop", d)!.AccountId);

        // Nested roots (a synced library inside another account's folder): the longest root wins.
        var nested = Build(
            Key("Personal", @"C:\Users\Test\OneDrive"),
            Key("Business1", @"C:\Users\Test\Partage", displayName: "Company", libraries: @"C:\Users\Test\OneDrive\Equipe"));
        Assert.Equal("Business1", AccountOf(@"C:\Users\Test\OneDrive\Equipe\Docs", nested)!.AccountId);
        Assert.Equal("Personal", AccountOf(@"C:\Users\Test\OneDrive\Perso", nested)!.AccountId);
    }

    [Fact]
    public void Case5_no_match_means_onedrive_with_unidentified_account()
    {
        // A local folder is not OneDrive at all.
        var d = Build(Key("Personal", @"C:\Users\Test\OneDrive"));
        Assert.Null(OneDriveAccountService.Match(@"C:\Users\Test\Desktop", d.Roots));

        // OneDrive root known only through an environment variable (no registry account): never guessed.
        var envOnly = OneDriveAccountService.Build([], [@"C:\Users\Test\OneDrive - Contoso"]);
        var root = OneDriveAccountService.Match(@"C:\Users\Test\OneDrive - Contoso\Desktop", envOnly.Roots);
        Assert.NotNull(root);
        Assert.Null(root!.Account);
        Assert.Equal("OneDrive", root.Title);
        Assert.Equal("Compte non identifié", root.Detail);
    }

    [Fact]
    public void Case6_email_missing_but_organisation_available()
    {
        var a = AccountOf(@"C:\Users\Test\OneDrive - itelium\Desktop", Build(Key("Business1", @"C:\Users\Test\OneDrive - itelium", null, "itelium")));
        Assert.Equal("OneDrive · itelium", a!.Title);
        Assert.Null(a.Detail); // level 3: the title is enough, no fake second line
    }

    [Fact]
    public void Case7_organisation_missing_but_email_available()
    {
        var a = AccountOf(@"C:\Users\Test\OneDrive - itelium\Desktop", Build(Key("Business1", @"C:\Users\Test\OneDrive - itelium", "test@itelium.ch")));
        Assert.Equal("OneDrive", a!.Title); // level 2: never derived from the folder name
        Assert.Equal("test@itelium.ch", a.Detail);
    }

    [Fact]
    public void Case8_account_types_from_key_names()
    {
        var d = Build(
            Key("Personal", @"C:\P"),
            Key("Business1", @"C:\B1"),
            Key("Business2", @"C:\B2"),
            Key("Autre", @"C:\X", business: true),
            Key("Inconnu", @"C:\Y"));
        Assert.Equal(OneDriveAccountType.Personal, d.Accounts.Single(a => a.AccountId == "Personal").AccountType);
        Assert.Equal(OneDriveAccountType.Business, d.Accounts.Single(a => a.AccountId == "Business1").AccountType);
        Assert.Equal(OneDriveAccountType.Business, d.Accounts.Single(a => a.AccountId == "Business2").AccountType);
        Assert.Equal(OneDriveAccountType.Business, d.Accounts.Single(a => a.AccountId == "Autre").AccountType);
        Assert.Equal(OneDriveAccountType.Unknown, d.Accounts.Single(a => a.AccountId == "Inconnu").AccountType);
        // The raw key name is never shown as the account label.
        Assert.DoesNotContain(d.Accounts, a => a.Title.Contains("Business") || (a.Detail ?? "").Contains("Business"));
    }

    [Fact]
    public void Short_labels_for_small_spaces()
    {
        var d = Build(
            Key("Personal", @"C:\P"),
            Key("Business1", @"C:\B1", null, "itelium"),
            Key("Business2", @"C:\B2", "x@client.ch", "Client SA"),
            Key("Business3", @"C:\B3"));
        Assert.Equal("Compte Personnel", d.Accounts[0].ShortLabel);
        Assert.Equal("Compte itelium", d.Accounts[1].ShortLabel);
        Assert.Equal("x@client.ch", d.Accounts[2].ShortLabel);
        Assert.Equal("Compte non identifié", d.Accounts[3].ShortLabel);
    }

    [Fact]
    public void Level4_business_without_any_identification()
    {
        var a = AccountOf(@"C:\B\Desktop", Build(Key("Business1", @"C:\B")));
        Assert.Equal("OneDrive", a!.Title);
        Assert.Equal("Compte non identifié", a.Detail);
    }

    [Fact]
    public void Personal_display_name_is_never_used_as_an_organisation()
    {
        var a = AccountOf(@"C:\P\Docs", Build(Key("Personal", @"C:\P", null, "Personal")));
        Assert.Equal("OneDrive · Personnel", a!.Title);
        Assert.Null(a.TenantDisplayName);
    }

    [Fact]
    public void Account_key_without_local_folder_is_ignored()
    {
        var d = Build(Key("Personal", @"C:\Users\Test\OneDrive"), Key("Business1", null));
        Assert.Single(d.Accounts);
        Assert.Equal(["Business1"], d.IgnoredKeys);
    }

    [Fact]
    public void Same_root_claimed_by_two_accounts_is_left_unidentified()
    {
        var d = Build(Key("Business1", @"C:\Users\Test\OneDrive - X", "a@x.ch", "X"), Key("Business2", @"c:\users\test\onedrive - x\", "b@y.ch", "Y"));
        var root = OneDriveAccountService.Match(@"C:\Users\Test\OneDrive - X\Desktop", d.Roots);
        Assert.NotNull(root);
        Assert.Null(root!.Account);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\OneDrive - Company\", @"c:/users/test/onedrive - company/Desktop")]      // trailing \, slashes, case
    [InlineData(@"C:\Users\Test\OneDrive - Company", @"\\?\C:\Users\Test\OneDrive - Company\Desktop")]   // extended prefix
    [InlineData(@"C:\Users\Test\Docs\..\OneDrive - Company", @"C:\Users\Test\OneDrive - Company\Desktop")] // ".."
    public void Paths_are_normalised_before_matching(string userFolder, string knownFolder)
    {
        var d = Build(Key("Business1", userFolder, displayName: "Company"));
        Assert.Equal("Business1", AccountOf(knownFolder, d)?.AccountId);
    }

    [Fact]
    public void Environment_root_already_known_from_the_registry_is_not_duplicated()
    {
        var d = OneDriveAccountService.Build([Key("Personal", @"C:\Users\Test\OneDrive")], [@"C:\Users\Test\OneDrive", null, ""]);
        Assert.Single(d.Roots);
        Assert.NotNull(d.Roots[0].Account);
    }

    [Fact]
    public async Task Log_reports_accounts_and_mapping_without_email_or_secret()
    {
        using var t = new TempTree();
        var oneDriveRoot = t.Dir("OneDrive - itelium");
        var docs = t.Dir(@"OneDrive - itelium\Documents");
        File.WriteAllText(Path.Combine(docs, "note.txt"), "x");
        var detection = Build(Key("Business1", oneDriveRoot, "secret.user@itelium.ch", "itelium"));
        var def = KnownFolders.Personal.First(d => d.Kind == PersonalFolderKind.Documents);
        var info = new PersonalFolderInfo(def, docs, true, FolderLocation.OneDrive, OneDriveAccountService.Match(docs, detection.Roots));
        var selection = new BackupSelection { Profile = CountersTests.FakeProfile(t.Root), OneDrive = detection };
        selection.PersonalRoots.Add(new PersonalRoot(info));

        using var target = new TempTree();
        var plan = selection.BuildPlan(target.Dir("Dest"), "Backup");
        await new CopyEngine(plan, new CopyProgress()).RunAsync(CancellationToken.None);
        var log = File.ReadAllText(Path.Combine(plan.BackupRoot, "copy-log.txt"));

        Assert.Contains(" ONEDRIVE", log);
        Assert.Contains("Compte OneDrive détecté : itelium (Professionnel, clé Business1)", log);
        Assert.Contains("Documents -> OneDrive « itelium »", log);
        Assert.DoesNotContain("secret.user@itelium.ch", log);
    }
}
