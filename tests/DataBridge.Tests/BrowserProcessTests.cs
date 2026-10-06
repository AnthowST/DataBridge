using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;

namespace DataBridge.Tests;

/// <summary>V1.2: browser image-name mapping and the "nothing to close" path (no process launched here).</summary>
public class BrowserProcessTests
{
    [Theory]
    [InlineData(BrowserKind.Chrome, "chrome")]
    [InlineData(BrowserKind.Edge, "msedge")]
    [InlineData(BrowserKind.Firefox, "firefox")]
    public void Process_name_matches_the_browser_executable(BrowserKind kind, string expected) =>
        Assert.Equal(expected, BrowserProcessService.ProcessName(kind));

    [Fact]
    public void Edge_image_name_is_not_the_webview2_runtime()
    {
        // GetProcessesByName("msedge") never matches "msedgewebview2": Teams/Outlook/Widgets stay untouched.
        Assert.Equal("msedge", BrowserProcessService.ProcessName(BrowserKind.Edge));
        Assert.NotEqual("msedgewebview2", BrowserProcessService.ProcessName(BrowserKind.Edge));
    }

    [Fact]
    public void Closing_a_browser_that_is_not_running_reports_not_running_and_closed()
    {
        // Firefox is not running in the test host: the result is "was not running", already closed, 0 remaining.
        if (BrowserProcessService.IsRunning(BrowserKind.Firefox)) return; // skip if a dev happens to run Firefox
        var results = BrowserProcessService.Close([BrowserKind.Firefox]);
        var r = Assert.Single(results);
        Assert.False(r.WasRunning);
        Assert.True(r.Closed);
        Assert.Equal(0, r.RemainingProcesses);
    }

    [Fact]
    public void App_and_backup_names_carry_the_DataBridge_brand()
    {
        Assert.Equal("DataBridge", AppInfo.Name);
        Assert.Equal("Copiez, sauvegardez et restaurez vos données", AppInfo.Baseline);
        Assert.StartsWith("DataBridge_", BackupNaming.FolderName("PC", "User", DateTime.Now));
    }

    [Theory]
    [InlineData("DataBridge_PC-ANTHOW_Anthow_2026-10-06_13-30", true)]
    [InlineData("itelium_ProfileCopy_PC-ANTHOW_Anthow_2026-10-06_12-50", true)]
    [InlineData("ProfileCopy_PC-ANTHOW_Anthow_2026-10-05_23-54", true)]
    [InlineData("Sauvegarde perso", false)]
    public void Backup_folders_of_every_version_are_recognised_by_name(string name, bool expected) =>
        Assert.Equal(expected, BackupNaming.HasKnownPrefix(name));

    [Fact]
    public void A_renamed_backup_is_still_recognised_by_its_log_not_only_its_name()
    {
        using var t = new TempTree();
        var renamed = t.Dir("Sauvegarde de Paul");
        // Log written by the first version of the tool (old product name in the title line).
        File.WriteAllLines(Path.Combine(renamed, "copy-log.txt"), ["=====", " ProfileCopy — Journal de sauvegarde du profil", "====="]);
        Assert.True(BackupNaming.LooksLikeBackup(renamed));
        Assert.False(BackupNaming.LooksLikeBackup(t.Dir("Autre dossier")));
    }
}
