using DataBridge.Core.Detection;
using DataBridge.Core.Profile;

namespace DataBridge.Core.Restore;

/// <summary>
/// Test-only redirections (Debug builds set them from DATABRIDGE_TEST_* variables; the Release never does):
/// Known Folders, roaming / local application data and DataBridge's own data folder.
/// </summary>
public sealed record RestoreTargetOverrides(
    IReadOnlyDictionary<PersonalFolderKind, string>? Folders = null,
    string? RoamingAppData = null,
    string? LocalAppData = null,
    bool SkipWallpaperApply = false);

/// <summary>
/// The current Windows session the backup is restored into. Destinations always come from here (current Known Folders,
/// current %APPDATA%...), never from the absolute paths recorded on the old PC.
/// </summary>
public sealed class RestoreTarget
{
    public required ProfileInfo Profile { get; init; }
    public required IReadOnlyList<PersonalFolderInfo> PersonalFolders { get; init; }
    public required OneDriveDetection OneDrive { get; init; }
    public required string RoamingAppData { get; init; }
    public required string LocalAppData { get; init; }
    public required IReadOnlyList<BrowserInfo> Browsers { get; init; }
    public bool SkipWallpaperApply { get; init; }

    public string SignaturesPath => Path.Combine(RoamingAppData, @"Microsoft\Signatures");
    public string TranscodedWallpaperPath => Path.Combine(RoamingAppData, @"Microsoft\Windows\Themes\TranscodedWallpaper");
    /// <summary>%LOCALAPPDATA%\DataBridge: restore logs and safety copies (never inside the backup, which may be read-only).</summary>
    public string DataFolder => Path.Combine(LocalAppData, AppInfo.Name);
    public string LogsFolder => Path.Combine(DataFolder, "Logs");
    public string SafetyBase => Path.Combine(DataFolder, "RestoreSafety");
    /// <summary>%APPDATA%\DataBridge\Wallpaper: stable copy of the restored wallpaper that Windows keeps pointing to.</summary>
    public string WallpaperStore => Path.Combine(RoamingAppData, AppInfo.Name, "Wallpaper");

    public PersonalFolderInfo? Folder(PersonalFolderKind kind) => PersonalFolders.FirstOrDefault(f => f.Definition.Kind == kind);
    public BrowserInfo? Browser(BrowserKind kind) => Browsers.FirstOrDefault(b => b.Kind == kind);

    public static RestoreTarget Detect(RestoreTargetOverrides? overrides = null)
    {
        var profile = ProfileInfo.Detect();
        var oneDrive = OneDriveAccountService.Detect();
        var roaming = overrides?.RoamingAppData ?? profile.RoamingAppData;
        var local = overrides?.LocalAppData ?? profile.LocalAppData;
        return new RestoreTarget
        {
            Profile = profile,
            PersonalFolders = PersonalFolderInfo.DetectAll(oneDrive.Roots, overrides?.Folders),
            OneDrive = oneDrive,
            RoamingAppData = roaming,
            LocalAppData = local,
            Browsers = BrowserDetector.DetectAll(local, roaming),
            SkipWallpaperApply = overrides?.SkipWallpaperApply ?? false,
        };
    }

    /// <summary>Detects the browsers again (a profile may have been created since).</summary>
    public RestoreTarget WithFreshBrowsers() => new()
    {
        Profile = Profile,
        PersonalFolders = PersonalFolders,
        OneDrive = OneDrive,
        RoamingAppData = RoamingAppData,
        LocalAppData = LocalAppData,
        Browsers = BrowserDetector.DetectAll(LocalAppData, RoamingAppData),
        SkipWallpaperApply = SkipWallpaperApply,
    };
}
