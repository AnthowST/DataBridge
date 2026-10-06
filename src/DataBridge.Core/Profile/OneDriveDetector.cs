namespace DataBridge.Core.Profile;

/// <summary>OneDrive roots of the current user, each linked to its account when it can be identified (see <see cref="OneDriveAccountService"/>).</summary>
public static class OneDriveDetector
{
    public static IReadOnlyList<OneDriveRoot> Detect() => OneDriveAccountService.Detect().Roots;

    /// <summary>Returns the OneDrive root containing <paramref name="path"/> (most specific match), or null when the path is local.</summary>
    public static OneDriveRoot? Classify(string path, IReadOnlyList<OneDriveRoot> roots) => OneDriveAccountService.Match(path, roots);
}
