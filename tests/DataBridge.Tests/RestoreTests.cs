using System.Text.Json;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Profile;
using DataBridge.Core.Restore;
using DataBridge.Core.Selection;

namespace DataBridge.Tests;

/// <summary>2.0 restore: backup reading (manifest / legacy), Known Folder mapping, path safety, conflicts, cancellation.</summary>
public class RestoreTests
{
    // ------------------------------------------------------------------ helpers

    internal static ProfileInfo Profile(string profilePath, string user = "New") =>
        new("PC-NOUVEAU", user, "PC-NOUVEAU", profilePath, Path.Combine(profilePath, @"AppData\Roaming"),
            Path.Combine(profilePath, @"AppData\Local"), PathUtilParent(profilePath), "Windows test", ElevationState.Standard);

    private static string? PathUtilParent(string p) => Path.GetDirectoryName(p.TrimEnd('\\'));

    internal static PersonalFolderInfo Folder(PersonalFolderKind kind, string path, FolderLocation location = FolderLocation.Local, OneDriveRoot? od = null) =>
        new(KnownFolders.Personal.First(d => d.Kind == kind), path, Directory.Exists(path), location, od);

    /// <summary>Current session of the restore: Known Folders and app data under the temporary tree.</summary>
    internal static RestoreTarget Target(TempTree t, string profileFolder = @"Users\New", params PersonalFolderInfo[] folders)
    {
        var profile = Profile(t.Dir(profileFolder));
        var list = folders.Length > 0 ? folders.ToList()
            : KnownFolders.Personal.Select(d => Folder(d.Kind, t.Dir(Path.Combine(profileFolder, d.DestinationName)))).ToList();
        var roaming = t.Dir(Path.Combine(profileFolder, @"AppData\Roaming"));
        var local = t.Dir(Path.Combine(profileFolder, @"AppData\Local"));
        return new RestoreTarget
        {
            Profile = profile,
            PersonalFolders = list,
            OneDrive = OneDriveDetection.Empty,
            RoamingAppData = roaming,
            LocalAppData = local,
            Browsers = BrowserDetector.DetectAll(local, roaming),
            SkipWallpaperApply = true,
        };
    }

    /// <summary>A DataBridge 2.0 backup produced by the real backup engine (with its manifest).</summary>
    internal static string RealBackup(TempTree t, Action<TempTree>? fill = null, string sourceUser = "Old", Action<BackupSelection>? select = null)
    {
        var sourceProfile = t.Dir($@"Users\{sourceUser}");
        var docs = t.Dir($@"Users\{sourceUser}\Documents");
        t.File($@"Users\{sourceUser}\Documents\Clients\facture.xlsx", 1200);
        t.File($@"Users\{sourceUser}\Documents\notes.txt", 300);
        t.File($@"Users\{sourceUser}\Desktop\raccourci.txt", 50);
        fill?.Invoke(t);
        var selection = new BackupSelection { Profile = Profile(sourceProfile, sourceUser) with { MachineName = "PC-ANCIEN" } };
        selection.PersonalRoots.Add(new PersonalRoot(Folder(PersonalFolderKind.Documents, docs)));
        selection.PersonalRoots.Add(new PersonalRoot(Folder(PersonalFolderKind.Desktop, t.Dir($@"Users\{sourceUser}\Desktop"))));
        select?.Invoke(selection);
        var plan = selection.BuildPlan(t.Dir("Backups"), "DataBridge_PC-ANCIEN_Old_2026-10-06_17-11");
        var result = new CopyEngine(plan, new CopyProgress()).RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(CopyOutcome.Completed, result.Outcome);
        Assert.NotNull(result.ManifestPath);
        return plan.BackupRoot;
    }

    private static RestoreResult Run(RestorePlan plan, CancellationToken ct = default) => new RestoreEngine(plan, new RestoreProgress()).Run(ct);

    // ------------------------------------------------------------------ manifest

    [Fact]
    public void New_backup_writes_a_manifest_without_secrets()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var json = File.ReadAllText(Path.Combine(root, BackupManifest.FileName));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(AppInfo.Version, doc.RootElement.GetProperty("dataBridgeVersion").GetString());
        Assert.Equal("PC-ANCIEN", doc.RootElement.GetProperty("source").GetProperty("computer").GetString());
        var personal = doc.RootElement.GetProperty("personalFolders");
        Assert.Equal(2, personal.GetArrayLength());
        Assert.Contains(personal.EnumerateArray(), p => p.GetProperty("relativePath").GetString() == @"Personal\Documents"
                                                        && p.GetProperty("files").GetInt64() == 2);
        foreach (var forbidden in new[] { "password", "keyMaterial", "token", "cookie", "secret" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manifest          : databridge-manifest.json", File.ReadAllText(Path.Combine(root, "copy-log.txt")));
    }

    [Fact]
    public void Manifest_backup_is_read_as_new_generation()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var open = BackupReader.Open(root);
        Assert.True(open.Success, open.Error);
        var c = open.Content!;
        Assert.Equal(BackupGeneration.Manifest, c.Generation);
        Assert.Equal("PC-ANCIEN", c.Source.Computer);
        Assert.Equal("Old", c.Source.User);
        Assert.Equal(AppInfo.Version, c.Source.DataBridgeVersion);
        Assert.Equal(3, c.TotalFiles);
        Assert.Equal([PersonalFolderKind.Desktop, PersonalFolderKind.Documents], c.Personal.Select(p => p.Kind));
        Assert.Empty(c.SecurityIssues);
    }

    [Fact]
    public void Future_manifest_schema_is_refused_cleanly()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var path = Path.Combine(root, BackupManifest.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99"));
        var open = BackupReader.Open(root);
        Assert.False(open.Success);
        Assert.Contains("version de format plus récente", open.Error);
        Assert.Contains("Mettez DataBridge à jour", open.Error);
    }

    [Fact]
    public void Folder_that_is_not_a_backup_is_refused_and_candidates_are_listed()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        t.File(@"Random\readme.txt", 10);
        var random = BackupReader.Open(t.P("Random"));
        Assert.False(random.Success);
        Assert.Contains("n'est pas une sauvegarde DataBridge", random.Error);

        var parent = BackupReader.Open(t.P("Backups"));
        Assert.False(parent.Success);
        Assert.Contains(root, parent.Candidates);
    }

    // ------------------------------------------------------------------ legacy

    private static string LegacyBackup(TempTree t, string prefix = "ProfileCopy_")
    {
        var root = t.Dir($@"Old\{prefix}PC-ANCIEN_Anthony_2026-10-05_23-54");
        t.File(Path.Combine(root, @"Personal\Documents\Clients\a.txt"), 100);
        t.File(Path.Combine(root, @"Additional\Compta\bilan.xlsx"), 200);
        t.File(Path.Combine(root, @"Outlook\Signatures\Externe.htm"), 20);
        t.File(Path.Combine(root, @"Windows\Wallpaper\TranscodedWallpaper"), 64);
        File.WriteAllText(Path.Combine(root, @"Network\WiFi\Wi-Fi-Bureau.xml").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)),
            "<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>Bureau</name>" +
            "<MSM><security><authEncryption><authentication>WPA2PSK</authentication></authEncryption>" +
            "<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>SECRET-KEY-123</keyMaterial></sharedKey></security></MSM></WLANProfile>");
        File.WriteAllText(Path.Combine(root, "copy-log.txt"),
            """
            ==============================================================================
             itelium ProfileCopy — Journal de sauvegarde du profil
            ==============================================================================
            Date              : 05.10.2026 23:54:10
            PC                : PC-ANCIEN
            Session           : Anthony (domaine CORP)
            Profil            : C:\Users\Anthony
            Windows           : Windows 11 Pro
            Statut            : Sauvegarde terminée

            ------------------------------------------------------------------------------
             RÉSUMÉ
            ------------------------------------------------------------------------------
            Fichiers copiés                        : 4

            ------------------------------------------------------------------------------
             ÉLÉMENTS COPIÉS (par sélection)
            ------------------------------------------------------------------------------

            [Dossiers personnels] Documents
              Source originale : C:\Users\Anthony\Documents
              Destination      : Personal\Documents
              Résultat         : 1 fichier copié, 100 o

            [Fichiers supplémentaires] Compta
              Source originale : C:\Users\Anthony\Compta
              Destination      : Additional\Compta
              Résultat         : 1 fichier copié, 200 o
            """);
        return root;
    }

    [Theory]
    [InlineData("ProfileCopy_")]
    [InlineData("itelium_ProfileCopy_")]
    [InlineData("DataBridge_")]
    public void Legacy_backup_is_reconstructed_from_structure_and_log(string prefix)
    {
        using var t = new TempTree();
        var root = LegacyBackup(t, prefix);
        var open = BackupReader.Open(root);
        Assert.True(open.Success, open.Error);
        var c = open.Content!;
        Assert.Equal(BackupGeneration.Legacy, c.Generation);
        Assert.Equal("Sauvegarde DataBridge ancienne génération", c.GenerationLabel);
        Assert.Equal("PC-ANCIEN", c.Source.Computer);
        Assert.Equal("Anthony", c.Source.User);
        Assert.Equal(@"C:\Users\Anthony", c.Source.ProfilePath);
        Assert.Equal(new DateTime(2026, 10, 5, 23, 54, 10), c.Source.CreatedAt);
        Assert.Equal(@"C:\Users\Anthony\Documents", c.Personal.Single().OriginalPath);
        Assert.Equal(@"C:\Users\Anthony\Compta", c.Additional.Single().OriginalPath);
        Assert.NotNull(c.Signatures);
        Assert.NotNull(c.Wallpaper);
        var wifi = c.Wifi.Single();
        Assert.Equal("Bureau", wifi.Name);
        Assert.True(wifi.KeyIncluded);
        Assert.Equal("Clé incluse", wifi.KeyLabel);
        // The key read from the XML never leaves the file.
        Assert.DoesNotContain("SECRET-KEY-123", JsonSerializer.Serialize(c.Wifi));
    }

    [Fact]
    public void Legacy_backup_without_original_path_is_never_guessed()
    {
        using var t = new TempTree();
        var root = LegacyBackup(t);
        t.File(Path.Combine(root, @"Additional\Inconnu\x.txt"), 5);
        var c = BackupReader.Open(root).Content!;
        var target = Target(t);
        var selection = RestoreSelection.Create(c, target);
        var unknown = selection.Additional.Single(a => a.Item.Name == "Inconnu");
        Assert.Equal(AdditionalDestinationKind.Undefined, unknown.Kind);
        Assert.Null(unknown.DestinationPath);
        var issues = RestorePlanner.Validate(selection, RestorePlanner.BuildPlan(selection));
        Assert.Contains(issues, i => i.IsBlocking && i.Message.StartsWith("Inconnu : destination à définir"));

        // Once a destination is chosen, the launch is no longer blocked by this element.
        unknown.CustomParent = t.Dir("Choix");
        Assert.DoesNotContain(RestorePlanner.Validate(selection, RestorePlanner.BuildPlan(selection)), i => i.IsBlocking);
    }

    // ------------------------------------------------------------------ Known Folders / OneDrive

    [Fact]
    public void Known_folder_of_the_old_user_goes_to_the_current_user_known_folder()
    {
        using var t = new TempTree();
        var root = RealBackup(t, sourceUser: "Old");
        var target = Target(t, @"Users\New");
        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        var plan = RestorePlanner.BuildPlan(selection);
        var docs = plan.Items.Single(i => i.Label == "Documents");
        Assert.Equal(t.P(@"Users\New\Documents"), docs.DestinationPath);
        Assert.DoesNotContain(@"\Users\Old\", docs.DestinationPath);

        var result = Run(plan);
        Assert.Equal(RestoreOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(t.P(@"Users\New\Documents\Clients\facture.xlsx")));
        Assert.True(File.Exists(t.P(@"Users\New\Desktop\raccourci.txt")));
        Assert.Equal(3, result.RestoredFiles);
        Assert.Equal(File.GetLastWriteTimeUtc(t.P(@"Users\Old\Documents\notes.txt")), File.GetLastWriteTimeUtc(t.P(@"Users\New\Documents\notes.txt")));
    }

    [Fact]
    public void Documents_redirected_to_OneDrive_on_the_new_pc_are_restored_into_OneDrive()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var oneDriveDocs = t.Dir(@"Users\New\OneDrive - Société\Documents");
        var account = new OneDriveAccount("Business1", OneDriveAccountType.Business, t.P(@"Users\New\OneDrive - Société"), [t.P(@"Users\New\OneDrive - Société")],
            "user@societe.ch", null, "Société");
        var odRoot = new OneDriveRoot(t.P(@"Users\New\OneDrive - Société"), account);
        var target = Target(t, @"Users\New",
            Folder(PersonalFolderKind.Documents, oneDriveDocs, FolderLocation.OneDrive, odRoot),
            Folder(PersonalFolderKind.Desktop, t.Dir(@"Users\New\Desktop")));
        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        var plan = RestorePlanner.BuildPlan(selection);
        var docs = plan.Items.Single(i => i.Label == "Documents");
        Assert.Equal(oneDriveDocs, docs.DestinationPath);
        Assert.True(docs.DestinationIsOneDrive);
        Assert.Equal("OneDrive · Société · user@societe.ch", docs.DestinationNote);
        Assert.Contains(RestorePlanner.Validate(selection, plan), i => !i.IsBlocking && i.Message.Contains("synchronisés par OneDrive"));

        Run(plan);
        Assert.True(File.Exists(Path.Combine(oneDriveDocs, @"Clients\facture.xlsx")));
        Assert.False(Directory.Exists(t.P(@"Users\New\Documents\Clients")));
    }

    [Fact]
    public void Additional_element_of_the_old_profile_is_adapted_to_the_current_profile()
    {
        using var t = new TempTree();
        var root = LegacyBackup(t);
        var target = Target(t);
        var content = BackupReader.Open(root).Content!;
        var (path, kind, note) = RestoreSelection.ProposeDestination(content.Additional.Single(), content, target);
        Assert.Equal(AdditionalDestinationKind.CurrentProfile, kind);
        Assert.Equal(Path.Combine(target.Profile.ProfilePath, "Compta"), path);
        Assert.Contains("adapté au profil actuel", note);
    }

    [Fact]
    public void Additional_element_on_a_missing_drive_needs_a_new_destination()
    {
        using var t = new TempTree();
        var missing = Enumerable.Range('D', 23).Select(c => (char)c).First(c => !Directory.Exists($"{c}:\\"));
        var item = new BackupAdditionalItem("Compta", @"Additional\Compta", t.Dir("x"), false, $@"{missing}:\Compta", null, null);
        var content = new BackupContent { Root = t.Root, Generation = BackupGeneration.Manifest, Source = new BackupSourceInfo(null, null, null, null, null, null, null, null) };
        var (path, kind, note) = RestoreSelection.ProposeDestination(item, content, Target(t));
        Assert.Null(path);
        Assert.Equal(AdditionalDestinationKind.Undefined, kind);
        Assert.Contains("Destination originale indisponible", note);
    }

    // ------------------------------------------------------------------ path safety

    [Theory]
    [InlineData(@"Personal\Documents", true)]
    [InlineData(@"Personal/Documents/", true)]
    [InlineData(@"..\..\Windows\System32\test.txt", false)]
    [InlineData(@"Personal\..\..\Windows", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"\Windows", false)]
    [InlineData(@"Personal\doc.txt:stream", false)]
    [InlineData(@"Personal\CON", false)]
    [InlineData(@"Personal\.", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    [InlineData("", false)]
    public void Relative_paths_of_a_backup_are_strictly_checked(string relative, bool safe)
    {
        Assert.Equal(safe, RestorePaths.SafeSegments(relative) != null);
        using var t = new TempTree();
        var resolved = RestorePaths.ResolveInside(t.Root, relative);
        Assert.Equal(safe, resolved != null);
        if (resolved != null) Assert.StartsWith(t.Root, resolved);
    }

    [Fact]
    public void Traversal_in_a_manifest_makes_the_plan_invalid_and_nothing_is_written()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var manifestPath = Path.Combine(root, BackupManifest.FileName);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), BackupManifest.JsonOptions)!;
        manifest.AdditionalItems.Add(new ManifestAdditionalItem
        {
            Name = "test.txt", OriginalPath = @"C:\Windows\System32\test.txt", RelativePath = @"..\..\Windows\System32\test.txt", IsFile = true,
        });
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, BackupManifest.JsonOptions));

        var content = BackupReader.Open(root).Content!;
        Assert.NotEmpty(content.SecurityIssues);
        Assert.DoesNotContain(content.Additional, a => a.Name == "test.txt");

        var target = Target(t);
        var selection = RestoreSelection.Create(content, target);
        var plan = RestorePlanner.BuildPlan(selection);
        var issues = RestorePlanner.Validate(selection, plan);
        Assert.Contains(issues, i => i.IsBlocking && i.Message.Contains("chemin refusé"));
    }

    [Fact]
    public void Engine_never_writes_outside_the_item_destination()
    {
        using var t = new TempTree();
        var backup = t.Dir("Backup");
        t.File(@"Backup\Personal\Documents\ok.txt", 10);
        var content = new BackupContent { Root = backup, Generation = BackupGeneration.Manifest, Source = new BackupSourceInfo(null, null, null, null, null, null, null, null) };
        var target = Target(t);
        var outside = t.P(@"Outside\evil.txt");
        // Hand-made unsafe item (a malformed plan): source outside the backup.
        var plan = new RestorePlan
        {
            Backup = content,
            Target = target,
            SafetyRoot = t.P("Safety"),
            Items =
            [
                new RestoreItem
                {
                    Category = RestoreCategories.Additional, Label = "evil", SourcePath = t.File(@"NotBackup\evil.txt", 5),
                    RelativeSourcePath = @"..\NotBackup\evil.txt", DestinationPath = outside, IsFile = true,
                },
            ],
        };
        var result = Run(plan);
        Assert.False(File.Exists(outside));
        Assert.Equal(1, result.Count(IssueKind.UnsafePath));
    }

    [Fact]
    public void Junction_inside_the_backup_is_never_followed()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var secret = t.Dir("Secret");
        t.File(@"Secret\private.txt", 10);
        var link = Path.Combine(root, @"Personal\Documents\Lien");
        if (!TryJunction(link, secret)) return; // junctions unavailable on this file system: nothing to test
        var target = Target(t);
        var plan = RestorePlanner.BuildPlan(RestoreSelection.Create(BackupReader.Open(root).Content!, target));
        var result = Run(plan);
        Assert.False(File.Exists(t.P(@"Users\New\Documents\Lien\private.txt")));
        Assert.Equal(1, result.Count(IssueKind.Link));
    }

    private static bool TryJunction(string link, string target)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false });
            p!.WaitForExit();
            return Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ conflicts

    private static (RestorePlan Plan, string Existing, string Older, string Newer) ConflictSetup(TempTree t, ConflictPolicy policy)
    {
        var root = RealBackup(t, tt =>
        {
            tt.File(@"Users\Old\Documents\recent.txt", 100);
            tt.File(@"Users\Old\Documents\ancien.txt", 100);
            tt.File(@"Users\Old\Documents\pareil.txt", 100);
        });
        File.SetLastWriteTimeUtc(Path.Combine(root, @"Personal\Documents\recent.txt"), new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(root, @"Personal\Documents\ancien.txt"), new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var pareil = File.GetLastWriteTimeUtc(Path.Combine(root, @"Personal\Documents\pareil.txt"));

        var target = Target(t);
        // Destination: recent.txt older than the backup, ancien.txt newer than the backup, pareil.txt identical.
        var older = t.File(@"Users\New\Documents\recent.txt", 7);
        File.SetLastWriteTimeUtc(older, new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
        var newer = t.File(@"Users\New\Documents\ancien.txt", 9);
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        var same = t.File(@"Users\New\Documents\pareil.txt", 100);
        File.SetLastWriteTimeUtc(same, pareil);

        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        selection.Policy = policy;
        return (RestorePlanner.BuildPlan(selection), same, older, newer);
    }

    [Theory]
    [InlineData(ConflictPolicy.Replace, 100, 100)]
    [InlineData(ConflictPolicy.Keep, 7, 9)]
    [InlineData(ConflictPolicy.ReplaceIfNewer, 100, 9)]
    public void Conflict_policies_apply_to_real_files(ConflictPolicy policy, long recentSize, long ancienSize)
    {
        using var t = new TempTree();
        var (plan, same, older, newer) = ConflictSetup(t, policy);
        var analysis = RestorePlanner.Analyze(plan);
        var result = Run(plan);

        Assert.Equal(recentSize, new FileInfo(older).Length);
        Assert.Equal(ancienSize, new FileInfo(newer).Length);
        Assert.Equal(100, new FileInfo(same).Length);
        Assert.Equal(1, result.IdenticalFiles);
        // The summary analysis and the engine agree.
        Assert.Equal(analysis.ReplaceFiles, result.ReplacedFiles);
        Assert.Equal(analysis.KeepFiles, result.KeptFiles);
        Assert.Equal(analysis.NewFiles, result.RestoredFiles);
        Assert.Equal(analysis.WriteFiles, result.WrittenFiles);
        Assert.Empty(Directory.EnumerateFiles(t.P(@"Users\New\Documents"), "*.databridge-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Read_only_and_hidden_existing_files_can_be_replaced()
    {
        using var t = new TempTree();
        var (plan, _, older, _) = ConflictSetup(t, ConflictPolicy.Replace);
        File.SetAttributes(older, FileAttributes.ReadOnly | FileAttributes.Hidden);
        var result = Run(plan);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(100, new FileInfo(older).Length);
    }

    // ------------------------------------------------------------------ cancellation and log

    [Fact]
    public async Task Cancelled_restore_keeps_finished_files_and_logs_the_interruption()
    {
        using var t = new TempTree();
        var root = RealBackup(t, tt =>
        {
            for (var i = 0; i < 400; i++) tt.File($@"Users\Old\Documents\Lot\f{i:000}.bin", 2048);
        });
        var target = Target(t);
        var plan = RestorePlanner.BuildPlan(RestoreSelection.Create(BackupReader.Open(root).Content!, target));
        var progress = new RestoreProgress();
        using var cts = new CancellationTokenSource();
        var task = new RestoreEngine(plan, progress).RunAsync(cts.Token);
        SpinWait.SpinUntil(() => Interlocked.Read(ref progress.RestoredFiles) >= 20, TimeSpan.FromSeconds(30));
        cts.Cancel();
        var result = await task;

        Assert.Equal(RestoreOutcome.Cancelled, result.Outcome);
        var restored = Directory.EnumerateFiles(t.P(@"Users\New\Documents"), "*", SearchOption.AllDirectories).ToList();
        Assert.InRange(restored.Count, 20, 402);
        Assert.True(restored.Count < 403);
        // Every file present is complete (no partial or temporary file).
        Assert.All(restored.Where(f => f.EndsWith(".bin")), f => Assert.Equal(2048, new FileInfo(f).Length));
        Assert.DoesNotContain(restored, f => f.EndsWith(".databridge-tmp"));
        Assert.True(result.NotProcessedFiles > 0);
        var log = File.ReadAllText(result.LogPath!);
        Assert.Contains("Restauration interrompue par l'utilisateur.", log);
        Assert.StartsWith(target.LogsFolder, result.LogPath);
        Assert.Matches(@"restore-\d{4}-\d{2}-\d{2}_\d{2}-\d{2}(_\d+)?\.log$", result.LogPath);
    }

    [Fact]
    public void Restore_log_lists_mappings_and_never_a_wifi_key()
    {
        using var t = new TempTree();
        var root = LegacyBackup(t);
        var target = Target(t);
        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        selection.SelectedWifi.Clear(); // no change to this PC's Wi-Fi in a unit test
        var plan = RestorePlanner.BuildPlan(selection);
        var result = Run(plan);
        var log = File.ReadAllText(result.LogPath!);
        Assert.Contains("Journal de restauration", log);
        Assert.Contains("PC source                  : PC-ANCIEN", log);
        Assert.Contains("Utilisateur source         : Anthony", log);
        Assert.Contains(@"Sauvegarde   : Personal\Documents", log);
        Assert.Contains($"Destination  : {t.P(@"Users\New\Documents")}", log);
        Assert.DoesNotContain("SECRET-KEY-123", log);
        Assert.True(File.Exists(t.P(@"Users\New\AppData\Roaming\Microsoft\Signatures\Externe.htm")));
    }

    // ------------------------------------------------------------------ browsers

    private static string BrowserBackup(TempTree t)
    {
        var root = t.Dir(@"Old\DataBridge_PC-ANCIEN_Old_2026-10-06_10-00");
        var profile = Path.Combine(root, @"Browsers\Chrome\User Data\Default");
        t.File(Path.Combine(profile, "Bookmarks"), 300);
        t.File(Path.Combine(profile, "Preferences"), 40);
        t.File(Path.Combine(profile, "History"), 500);
        t.File(Path.Combine(profile, "Login Data"), 77);
        t.File(Path.Combine(profile, @"Network\Cookies"), 66);
        t.File(Path.Combine(profile, "Web Data"), 55);
        t.File(Path.Combine(profile, @"Extensions\abc\1.0\manifest.json"), 20);
        File.WriteAllText(Path.Combine(root, @"Browsers\Chrome\User Data\Local State"),
            """{"profile":{"last_used":"Default","info_cache":{"Default":{"name":"Votre Chrome","user_name":"ancien@email.com"}}},"os_crypt":{"encrypted_key":"OLD"}}""");
        File.WriteAllText(Path.Combine(root, "copy-log.txt"), " DataBridge — Journal de sauvegarde du profil\r\n");
        return root;
    }

    [Fact]
    public void Chrome_profile_is_restored_without_non_portable_files_and_with_a_safety_copy()
    {
        using var t = new TempTree();
        var root = BrowserBackup(t);
        var target = Target(t);
        var userData = t.Dir(@"Users\New\AppData\Local\Google\Chrome\User Data");
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Default\Preferences", 5);
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Default\Bookmarks", 3);
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Default\History-journal", 4);
        File.WriteAllText(Path.Combine(userData, "Local State"), """{"profile":{"info_cache":{"Default":{"name":"Personne 1","user_name":"nouveau@email.com"}}},"os_crypt":{"encrypted_key":"NEW"}}""");
        target = target.WithFreshBrowsers();

        var content = BackupReader.Open(root).Content!;
        var saved = content.Browsers.Single().Profiles.Single();
        Assert.Equal("Votre Chrome", saved.DisplayName);
        Assert.Equal("ancien@email.com", saved.Account);

        var selection = RestoreSelection.Create(content, target);
        var choice = selection.Browsers.Single();
        Assert.NotNull(choice.Target); // single destination profile: chosen automatically
        Assert.Contains("nouveau@email.com", choice.Target!.Detail);
        var plan = RestorePlanner.BuildPlan(selection);
        var result = Run(plan);

        var dest = Path.Combine(userData, "Default");
        Assert.Equal(300, new FileInfo(Path.Combine(dest, "Bookmarks")).Length);
        Assert.True(File.Exists(Path.Combine(dest, @"Extensions\abc\1.0\manifest.json")));
        Assert.False(File.Exists(Path.Combine(dest, "Login Data")));
        Assert.False(File.Exists(Path.Combine(dest, @"Network\Cookies")));
        Assert.False(File.Exists(Path.Combine(dest, "Web Data")));
        // Local State of this PC (its DPAPI key) is never replaced.
        Assert.Contains("NEW", File.ReadAllText(Path.Combine(userData, "Local State")));
        // Safety copy of the replaced files, stale journal moved away.
        var safety = plan.Items.Single().SafetyFolder!;
        Assert.Equal(3, new FileInfo(Path.Combine(safety, "Bookmarks")).Length);
        Assert.False(File.Exists(Path.Combine(dest, "History-journal")));
        Assert.True(File.Exists(Path.Combine(safety, "History-journal")));
        Assert.Equal(1, result.Count(IssueKind.Restored));
    }

    [Fact]
    public void Complete_chrome_user_data_is_backed_up_and_restored_beside_the_profiles()
    {
        using var t = new TempTree();
        var source = t.Dir(@"Users\Old\AppData\Local\Google\Chrome\User Data");
        File.WriteAllText(Path.Combine(source, "Local State"),
            """{"profile":{"info_cache":{"Default":{"name":"Perso"},"Profile 1":{"name":"Travail"}}},"os_crypt":{"encrypted_key":"OLD"}}""");
        t.File(Path.Combine(source, @"Default\Preferences"), 40);
        t.File(Path.Combine(source, @"Default\Bookmarks"), 300);
        t.File(Path.Combine(source, @"Default\Cache\Cache_Data\data_0"), 900);
        t.File(Path.Combine(source, @"Profile 1\Preferences"), 41);
        t.File(Path.Combine(source, @"WidevineCdm\4.10\manifest.json"), 12);
        t.File(Path.Combine(source, "First Run"), 1);
        t.File(Path.Combine(source, @"GrShaderCache\data_1"), 800);
        var chrome = BrowserDetector.DetectChromium(BrowserKind.Chrome, "Google Chrome", "chrome", source);

        // Backup: the whole User Data folder, minus the caches and the unchecked profile.
        var root = RealBackup(t, select: s =>
        {
            var browser = new BrowserSelection(chrome);
            browser.SelectedProfiles.Remove(chrome.Profiles.Single(p => p.FolderName == "Profile 1").FullPath);
            s.Browsers.Add(browser);
        });
        var saved = Path.Combine(root, @"Browsers\Chrome\User Data");
        Assert.True(File.Exists(Path.Combine(saved, "Local State")));
        Assert.Equal(300, new FileInfo(Path.Combine(saved, @"Default\Bookmarks")).Length);
        Assert.True(File.Exists(Path.Combine(saved, @"WidevineCdm\4.10\manifest.json")));
        Assert.True(File.Exists(Path.Combine(saved, "First Run")));
        Assert.False(Directory.Exists(Path.Combine(saved, "Profile 1")));
        Assert.False(Directory.Exists(Path.Combine(saved, @"Default\Cache")));
        Assert.False(Directory.Exists(Path.Combine(saved, "GrShaderCache")));

        // Restore: the profile into the chosen profile, the rest of User Data beside it, this PC's Local State kept.
        var target = Target(t);
        var userData = t.Dir(@"Users\New\AppData\Local\Google\Chrome\User Data");
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Default\Preferences", 5);
        File.WriteAllText(Path.Combine(userData, "Local State"), "NEW");
        target = target.WithFreshBrowsers();
        var plan = RestorePlanner.BuildPlan(RestoreSelection.Create(BackupReader.Open(root).Content!, target));
        Assert.Contains(plan.Items, i => i.Label == BrowserRestoreService.CommonDataLabel("Google Chrome") && i.DestinationPath == PathUtil.Normalize(userData));
        var result = Run(plan);

        Assert.Equal(0, result.Count(IssueKind.WriteError));
        Assert.Equal(12, new FileInfo(Path.Combine(userData, @"WidevineCdm\4.10\manifest.json")).Length);
        Assert.True(File.Exists(Path.Combine(userData, "First Run")));
        Assert.Equal(300, new FileInfo(Path.Combine(userData, @"Default\Bookmarks")).Length);
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(userData, "Local State")));
        Assert.False(Directory.Exists(Path.Combine(userData, "Profile 1")));
    }

    [Fact]
    public void Several_destination_profiles_require_a_choice()
    {
        using var t = new TempTree();
        var root = BrowserBackup(t);
        var target = Target(t);
        var userData = t.Dir(@"Users\New\AppData\Local\Google\Chrome\User Data");
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Default\Preferences", 5);
        t.File(@"Users\New\AppData\Local\Google\Chrome\User Data\Profile 1\Preferences", 5);
        target = target.WithFreshBrowsers();
        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        Assert.Null(selection.Browsers.Single().Target);
        Assert.Contains(RestorePlanner.Validate(selection, RestorePlanner.BuildPlan(selection)),
            i => i.IsBlocking && i.Message.Contains("choisissez le profil de destination"));
    }

    [Fact]
    public void Firefox_profile_goes_to_a_new_registered_profile_when_none_exists()
    {
        using var t = new TempTree();
        var root = t.Dir(@"Old\DataBridge_PC-ANCIEN_Old_2026-10-06_10-00");
        t.File(Path.Combine(root, @"Browsers\Firefox\Profiles\ab12cd34.default-release\places.sqlite"), 400);
        t.File(Path.Combine(root, @"Browsers\Firefox\Profiles\ab12cd34.default-release\parent.lock"), 1);
        File.WriteAllText(Path.Combine(root, @"Browsers\Firefox\profiles.ini"),
            "[Profile0]\nName=default-release\nIsRelative=1\nPath=Profiles/ab12cd34.default-release\nDefault=1\n");
        File.WriteAllText(Path.Combine(root, "copy-log.txt"), " DataBridge — Journal de sauvegarde du profil\r\n");
        var target = Target(t);

        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        var choice = selection.Browsers.Single();
        Assert.True(choice.Target!.CreatesProfile);
        var result = Run(RestorePlanner.BuildPlan(selection));
        Assert.Equal(0, result.ErrorCount);

        var ini = File.ReadAllText(t.P(@"Users\New\AppData\Roaming\Mozilla\Firefox\profiles.ini"));
        Assert.Contains("Name=default-release (DataBridge)", ini);
        Assert.Contains("Default=1", ini);
        var dest = choice.Target.ProfilePath;
        Assert.True(File.Exists(Path.Combine(dest, "places.sqlite")));
        Assert.False(File.Exists(Path.Combine(dest, "parent.lock")));
        // Detected afterwards by the backup's own Firefox reader.
        var firefox = BrowserDetector.DetectFirefox(t.P(@"Users\New\AppData\Roaming\Mozilla\Firefox"));
        Assert.Contains(firefox.Profiles, p => p.IsActive && p.FullPath.Equals(dest, StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ space

    [Fact]
    public void Space_is_checked_per_volume()
    {
        using var t = new TempTree();
        var root = RealBackup(t);
        var target = Target(t);
        var selection = RestoreSelection.Create(BackupReader.Open(root).Content!, target);
        var plan = RestorePlanner.BuildPlan(selection);
        var analysis = RestorePlanner.Analyze(plan);
        var volume = Assert.Single(analysis.Volumes);
        Assert.Equal(1550, volume.NeededBytes);
        Assert.True(volume.Sufficient);

        var fake = new RestoreAnalysis();
        fake.Volumes.Add(new VolumeNeed(@"Q:\", 10L * 1024 * 1024 * 1024, 1024));
        Assert.Contains(RestorePlanner.Validate(selection, plan, fake), i => i.IsBlocking && i.Message.StartsWith("Espace insuffisant sur Q:"));
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
