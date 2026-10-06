using Microsoft.Win32;
using DataBridge.Core.IO;
using DataBridge.Core.Native;

namespace DataBridge.Core.Profile;

public enum ElevationState { Standard, LimitedAdmin, Elevated, Unknown }

/// <summary>Identity of the machine / session being backed up. Only the current user is ever considered.</summary>
public sealed record ProfileInfo(
    string MachineName,
    string UserName,
    string UserDomain,
    string ProfilePath,
    string RoamingAppData,
    string LocalAppData,
    string? UserProfilesRoot,
    string WindowsVersion,
    ElevationState Elevation)
{
    public bool IsElevated => Elevation == ElevationState.Elevated;

    public string ElevationLabel => Elevation switch
    {
        ElevationState.Elevated => "Exécuté en administrateur (élevé)",
        ElevationState.LimitedAdmin => "Droits utilisateur (non élevé)",
        ElevationState.Standard => "Droits utilisateur standard",
        _ => "Niveau de droits inconnu",
    };

    public static ProfileInfo Detect()
    {
        var profile = KnownFolders.GetPath(KnownFolders.Profile)
                      ?? PathUtil.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var roaming = KnownFolders.GetPath(KnownFolders.RoamingAppData)
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = KnownFolders.GetPath(KnownFolders.LocalAppData)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return new ProfileInfo(
            Environment.MachineName,
            Environment.UserName,
            Environment.UserDomainName,
            profile,
            roaming,
            local,
            KnownFolders.GetPath(KnownFolders.UserProfiles),
            ReadWindowsVersion(),
            ReadElevation());
    }

    private static string ReadWindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName") as string ?? "Windows";
            var display = key?.GetValue("DisplayVersion") as string;
            var buildText = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            // ProductName still says "Windows 10" on Windows 11.
            if (int.TryParse(buildText, out var build) && build >= 22000)
                product = product.Replace("Windows 10", "Windows 11");
            return display != null ? $"{product} {display} (build {buildText})" : $"{product} (build {buildText})";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private static ElevationState ReadElevation()
    {
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), NativeMethods.TOKEN_QUERY, out var token))
                return ElevationState.Unknown;
            using (token)
            {
                if (NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevated, 4, out _) && elevated != 0)
                    return ElevationState.Elevated;
                if (NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevationType, out var type, 4, out _) && type == 3)
                    return ElevationState.LimitedAdmin; // admin account running with its filtered (UAC) token
                return ElevationState.Standard;
            }
        }
        catch
        {
            return ElevationState.Unknown;
        }
    }

    /// <summary>
    /// True when the path lives in another user's profile (C:\Users\Someone). The application never
    /// reads other profiles; C:\Users\Public stays allowed.
    /// </summary>
    public bool IsInOtherUserProfile(string path)
    {
        if (UserProfilesRoot is null) return false;
        if (!PathUtil.IsStrictlyUnder(path, UserProfilesRoot)) return false;
        if (PathUtil.IsSameOrUnder(path, ProfilePath)) return false;
        var first = PathUtil.Relative(path, UserProfilesRoot).Split('\\')[0];
        return !first.Equals("Public", StringComparison.OrdinalIgnoreCase);
    }
}
