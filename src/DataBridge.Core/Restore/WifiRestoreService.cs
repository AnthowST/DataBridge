using System.Text;
using System.Xml.Linq;
using DataBridge.Core.Detection;
using DataBridge.Core.Native;

namespace DataBridge.Core.Restore;

public enum WifiRestoreOutcome { Imported, Replaced, KeptExisting, NotRestored, Failed }

public sealed record WifiRestoreResult(string ProfileName, WifiRestoreOutcome Outcome, string Message)
{
    public bool Restored => Outcome is WifiRestoreOutcome.Imported or WifiRestoreOutcome.Replaced;
}

/// <summary>State of the Wi-Fi service on this PC, for the restore page.</summary>
public sealed record WifiEnvironment(bool Available, string? Message, IReadOnlySet<string> ExistingProfiles);

/// <summary>
/// Re-imports saved Wi-Fi profiles (XML) with the standard WLAN API, with the rights of the session only: Windows
/// decides whether the user may add the profile. When it refuses, the profile is reported as not restored and nothing
/// is forced (no elevation, no UAC). The key that an XML may contain is never read into the interface or the log.
/// </summary>
public static class WifiRestoreService
{
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_ALREADY_EXISTS = 183;
    private const uint ERROR_BAD_PROFILE = 1206;
    private const uint ERROR_SERVICE_NOT_ACTIVE = 1062;
    private const long MaxProfileSize = 512 * 1024;

    /// <summary>Name, authentication and whether the key is included. The key itself is never returned.</summary>
    public static (string Name, string Authentication, bool KeyIncluded)? ReadProfileFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxProfileSize) return null;
            var xml = File.ReadAllText(path);
            var doc = XDocument.Parse(xml);
            if (doc.Root?.Name.LocalName != "WLANProfile") return null;
            var name = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
            if (string.IsNullOrWhiteSpace(name)) return null;
            var (auth, key) = WifiService.Analyze(xml);
            return (name, auth, key == WifiKeyStatus.Available);
        }
        catch
        {
            return null;
        }
    }

    public static WifiEnvironment Inspect()
    {
        try
        {
            var result = NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out var client);
            if (result != 0)
                return new WifiEnvironment(false, result == ERROR_SERVICE_NOT_ACTIVE
                    ? "Le service WLAN n'est pas démarré sur ce PC (aucune carte Wi-Fi ?) : les profils ne pourront pas être importés."
                    : $"API WLAN indisponible (code {result}).", new HashSet<string>());
            try
            {
                var interfaces = WifiService.EnumInterfaces(client);
                if (interfaces.Count == 0)
                    return new WifiEnvironment(false, "Aucune carte Wi-Fi détectée sur ce PC : les profils ne pourront pas être importés.", new HashSet<string>());
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (id, _) in interfaces) names.UnionWith(WifiService.ProfileNames(client, id));
                return new WifiEnvironment(true, null, names);
            }
            finally
            {
                NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
            }
        }
        catch (DllNotFoundException)
        {
            return new WifiEnvironment(false, "API WLAN absente sur ce système.", new HashSet<string>());
        }
    }

    /// <summary>Imports one saved profile into every Wi-Fi interface of this PC.</summary>
    public static WifiRestoreResult Import(BackupWifiProfile profile, WifiExistingPolicy existingPolicy)
    {
        string xml;
        try
        {
            var info = new FileInfo(profile.FullPath);
            if (!info.Exists) return new(profile.Name, WifiRestoreOutcome.Failed, "Fichier XML absent de la sauvegarde.");
            if (info.Length > MaxProfileSize) return new(profile.Name, WifiRestoreOutcome.Failed, "Fichier XML trop volumineux pour un profil Wi-Fi.");
            xml = File.ReadAllText(profile.FullPath);
            if (XDocument.Parse(xml).Root?.Name.LocalName != "WLANProfile")
                return new(profile.Name, WifiRestoreOutcome.Failed, "Fichier XML qui n'est pas un profil Wi-Fi Windows.");
        }
        catch (Exception ex)
        {
            return new(profile.Name, WifiRestoreOutcome.Failed, "Lecture du profil impossible : " + ex.Message);
        }

        IntPtr client;
        try
        {
            if (NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out client) != 0)
                return new(profile.Name, WifiRestoreOutcome.NotRestored, "Service WLAN indisponible sur ce PC.");
        }
        catch (DllNotFoundException)
        {
            return new(profile.Name, WifiRestoreOutcome.NotRestored, "API WLAN absente sur ce système.");
        }

        try
        {
            var interfaces = WifiService.EnumInterfaces(client);
            if (interfaces.Count == 0) return new(profile.Name, WifiRestoreOutcome.NotRestored, "Aucune carte Wi-Fi sur ce PC.");

            var exists = interfaces.Any(i => WifiService.ProfileNames(client, i.Id).Contains(profile.Name));
            if (exists && existingPolicy == WifiExistingPolicy.Keep)
                return new(profile.Name, WifiRestoreOutcome.KeptExisting, "Profil Wi-Fi déjà présent sur ce PC : conservé.");

            uint lastError = 0;
            string? reason = null;
            var imported = 0;
            foreach (var (id, _) in interfaces)
            {
                var iface = id;
                // All users first (like the original export), then the current user only when Windows refuses.
                var error = NativeMethods.WlanSetProfile(client, ref iface, profile.PerUser == true ? NativeMethods.WLAN_PROFILE_USER : 0,
                    xml, null, exists, IntPtr.Zero, out var reasonCode);
                if (error == ERROR_ACCESS_DENIED && profile.PerUser != true)
                    error = NativeMethods.WlanSetProfile(client, ref iface, NativeMethods.WLAN_PROFILE_USER, xml, null, exists, IntPtr.Zero, out reasonCode);
                if (error == 0) { imported++; continue; }
                lastError = error;
                if (error == ERROR_BAD_PROFILE) reason = ReasonText(reasonCode);
            }

            if (imported > 0)
                return new(profile.Name, exists ? WifiRestoreOutcome.Replaced : WifiRestoreOutcome.Imported,
                    exists ? "Profil Wi-Fi remplacé." : "Profil Wi-Fi importé.");
            return lastError switch
            {
                ERROR_ACCESS_DENIED => new(profile.Name, WifiRestoreOutcome.NotRestored, "Profil Wi-Fi non restauré avec les droits de la session."),
                ERROR_ALREADY_EXISTS => new(profile.Name, WifiRestoreOutcome.KeptExisting, "Profil Wi-Fi déjà présent sur ce PC : conservé."),
                ERROR_BAD_PROFILE => new(profile.Name, WifiRestoreOutcome.Failed, "Profil refusé par Windows" + (reason != null ? " : " + reason : ".")),
                _ => new(profile.Name, WifiRestoreOutcome.Failed, $"Import refusé par Windows (code {lastError})."),
            };
        }
        catch (Exception ex)
        {
            return new(profile.Name, WifiRestoreOutcome.Failed, ex.Message);
        }
        finally
        {
            NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static string? ReasonText(uint code)
    {
        try
        {
            var sb = new StringBuilder(512);
            return NativeMethods.WlanReasonCodeToString(code, (uint)sb.Capacity, sb, IntPtr.Zero) == 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes a profile by name from every interface (tests clean up the test profile they imported).</summary>
    internal static bool Delete(string name)
    {
        if (NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out var client) != 0) return false;
        try
        {
            var deleted = false;
            foreach (var (id, _) in WifiService.EnumInterfaces(client))
            {
                var iface = id;
                if (NativeMethods.WlanDeleteProfile(client, ref iface, name, IntPtr.Zero) == 0) deleted = true;
            }
            return deleted;
        }
        finally
        {
            NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }
}
