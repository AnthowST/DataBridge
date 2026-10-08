using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using DataBridge.Core.IO;
using DataBridge.Core.Native;

namespace DataBridge.Core.Detection;

public enum WifiKeyStatus { Unknown, Available, NotAccessible, NoKey }

public sealed record WifiProfileInfo(
    string Name,
    Guid InterfaceId,
    string InterfaceDescription,
    bool IsPerUser,
    bool IsGroupPolicy,
    string Authentication,
    WifiKeyStatus KeyStatus,
    bool KeyViaAdmin = false)
{
    public string ScopeLabel => IsGroupPolicy ? "Stratégie de groupe" : IsPerUser ? "Utilisateur actuel" : "Tous les utilisateurs";

    public string KeyLabel => KeyStatus switch
    {
        WifiKeyStatus.Available when KeyViaAdmin => "Clé récupérée avec les droits administrateur",
        WifiKeyStatus.Available => "Clé exportable",
        // Windows returns all-user keys in clear only to administrators; per-user keys stay protected by the user's DPAPI.
        WifiKeyStatus.NotAccessible when IsPerUser => "Clé protégée par Windows : non accessible avec les droits de la session",
        WifiKeyStatus.NotAccessible => "Clé non accessible sans droits administrateur",
        WifiKeyStatus.NoKey => "Pas de clé partagée (réseau ouvert ou entreprise)",
        _ => "Clé : état inconnu",
    };
}

public sealed record WifiDetection(bool ServiceAvailable, string? Message, IReadOnlyList<WifiProfileInfo> Profiles);

public sealed record WifiExportResult(string ProfileName, bool Exported, string? FileName, WifiKeyStatus KeyStatus, string? Error,
    bool KeyViaAdmin = false)
{
    public bool KeyIncluded => Exported && KeyStatus == WifiKeyStatus.Available;
}

/// <summary>
/// Wi-Fi profiles through the native WLAN API (wlanapi.dll) rather than parsing localized "netsh" output.
/// The key is requested with WLAN_PROFILE_GET_PLAINTEXT_KEY: Windows decides whether the current, non-elevated
/// user may read it. When it refuses, the profile is still exported (key protected / absent): the technician may
/// recover those keys through ONE UAC prompt (<see cref="WifiKeyElevation"/>), never automatically.
/// The key is never displayed or logged: only its availability is reported.
/// </summary>
public static class WifiService
{
    private const uint ERROR_SERVICE_NOT_ACTIVE = 1062;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const int InterfaceInfoSize = 16 + 512 + 4; // GUID + WCHAR[256] + state
    private const int ProfileInfoSize = 512 + 4;        // WCHAR[256] + flags

    public static WifiDetection Detect()
    {
        IntPtr client;
        uint result;
        try
        {
            result = NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out client);
        }
        catch (DllNotFoundException)
        {
            return new WifiDetection(false, "API WLAN absente sur ce système.", []);
        }

        if (result != 0)
        {
            var message = result == ERROR_SERVICE_NOT_ACTIVE
                ? "Le service de configuration automatique WLAN n'est pas démarré (aucune carte Wi-Fi ?)."
                : $"API WLAN indisponible (code {result}).";
            return new WifiDetection(false, message, []);
        }

        try
        {
            var profiles = new List<WifiProfileInfo>();
            foreach (var (id, description) in EnumInterfaces(client))
            {
                var ifaceId = id;
                if (NativeMethods.WlanGetProfileList(client, ref ifaceId, IntPtr.Zero, out var listPtr) != 0) continue;
                try
                {
                    var count = Marshal.ReadInt32(listPtr, 0);
                    for (var i = 0; i < count; i++)
                    {
                        var itemPtr = listPtr + 8 + i * ProfileInfoSize;
                        var name = Marshal.PtrToStringUni(itemPtr) ?? "";
                        var flags = (uint)Marshal.ReadInt32(itemPtr, 512);
                        if (name.Length == 0 || profiles.Any(p => p.Name == name)) continue;

                        var (auth, keyStatus) = Inspect(client, id, name);
                        profiles.Add(new WifiProfileInfo(name, id, description,
                            (flags & NativeMethods.WLAN_PROFILE_USER) != 0,
                            (flags & NativeMethods.WLAN_PROFILE_GROUP_POLICY) != 0,
                            auth, keyStatus));
                    }
                }
                finally
                {
                    NativeMethods.WlanFreeMemory(listPtr);
                }
            }

            return new WifiDetection(true, profiles.Count == 0 ? "Aucun profil Wi-Fi enregistré sur ce PC." : null, profiles);
        }
        finally
        {
            NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Writes the profile XML as returned by Windows into <paramref name="folder"/> (Wi-Fi-&lt;name&gt;.xml).
    /// <paramref name="recoveredXml"/>: the XML already read by the elevated worker (key recovered as administrator).
    /// </summary>
    public static WifiExportResult Export(WifiProfileInfo profile, string folder, string? recoveredXml = null)
    {
        if (recoveredXml != null) return Write(profile, folder, recoveredXml, viaAdmin: true);
        if (NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out var client) != 0)
            return new WifiExportResult(profile.Name, false, null, WifiKeyStatus.Unknown, "API WLAN indisponible.");
        try
        {
            var xml = GetProfileXml(client, profile.InterfaceId, profile.Name, out var error);
            if (xml is null)
            {
                var reason = error == ERROR_ACCESS_DENIED ? "Accès refusé par Windows." : $"Erreur WLAN {error}.";
                return new WifiExportResult(profile.Name, false, null, WifiKeyStatus.Unknown, reason);
            }
            return Write(profile, folder, xml, viaAdmin: false);
        }
        catch (Exception ex)
        {
            return new WifiExportResult(profile.Name, false, null, WifiKeyStatus.Unknown, ex.Message);
        }
        finally
        {
            NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }
    }

    private static WifiExportResult Write(WifiProfileInfo profile, string folder, string xml, bool viaAdmin)
    {
        try
        {
            var (_, keyStatus) = Analyze(xml);
            var fileName = "Wi-Fi-" + PathUtil.SanitizeSegment(profile.Name, "profil") + ".xml";
            var target = Path.Combine(folder, fileName);
            var n = 2;
            while (File.Exists(target))
                target = Path.Combine(folder, $"Wi-Fi-{PathUtil.SanitizeSegment(profile.Name, "profil")} ({n++}).xml");

            // Same encoding as the XML declaration says (WLAN profiles normally carry no encoding => UTF-8).
            var encoding = xml.Contains("encoding=\"UTF-16\"", StringComparison.OrdinalIgnoreCase) ? Encoding.Unicode : new UTF8Encoding(false);
            File.WriteAllText(target, xml, encoding);
            return new WifiExportResult(profile.Name, true, Path.GetFileName(target), keyStatus, null,
                viaAdmin && keyStatus == WifiKeyStatus.Available);
        }
        catch (Exception ex)
        {
            return new WifiExportResult(profile.Name, false, null, WifiKeyStatus.Unknown, ex.Message);
        }
    }

    private static (string Auth, WifiKeyStatus Key) Inspect(IntPtr client, Guid iface, string name)
    {
        var xml = GetProfileXml(client, iface, name, out _);
        return xml is null ? ("Inconnue", WifiKeyStatus.Unknown) : Analyze(xml);
    }

    /// <summary>Reads authentication type and key availability from a profile XML. Never returns the key itself.</summary>
    internal static (string Auth, WifiKeyStatus Key) Analyze(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var auth = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "authentication")?.Value ?? "";
            var sharedKey = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "sharedKey");
            WifiKeyStatus status;
            if (sharedKey is null)
                status = WifiKeyStatus.NoKey;
            else
            {
                var isProtected = sharedKey.Elements().FirstOrDefault(e => e.Name.LocalName == "protected")?.Value;
                var hasMaterial = sharedKey.Elements().Any(e => e.Name.LocalName == "keyMaterial" && e.Value.Length > 0);
                status = !hasMaterial ? WifiKeyStatus.NotAccessible
                    : string.Equals(isProtected, "false", StringComparison.OrdinalIgnoreCase) ? WifiKeyStatus.Available
                    : WifiKeyStatus.NotAccessible;
            }
            return (AuthLabel(auth), status);
        }
        catch
        {
            return ("Inconnue", WifiKeyStatus.Unknown);
        }
    }

    private static string AuthLabel(string auth) => auth.ToUpperInvariant() switch
    {
        "OPEN" => "Ouvert",
        "SHARED" => "WEP",
        "WPAPSK" => "WPA-Personnel",
        "WPA" => "WPA-Entreprise",
        "WPA2PSK" => "WPA2-Personnel",
        "WPA2" => "WPA2-Entreprise",
        "WPA3SAE" => "WPA3-Personnel",
        "WPA3" or "WPA3ENT" or "WPA3ENT192" => "WPA3-Entreprise",
        "OWE" => "OWE (ouvert chiffré)",
        "" => "Inconnue",
        _ => auth,
    };

    internal static string? GetProfileXml(IntPtr client, Guid iface, string name, out uint error)
    {
        uint flags = NativeMethods.WLAN_PROFILE_GET_PLAINTEXT_KEY;
        error = NativeMethods.WlanGetProfile(client, ref iface, name, IntPtr.Zero, out var xmlPtr, ref flags, out _);
        if (error != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(xmlPtr);
        }
        finally
        {
            NativeMethods.WlanFreeMemory(xmlPtr);
        }
    }

    /// <summary>Names of the profiles known for one interface (no XML, no key).</summary>
    internal static HashSet<string> ProfileNames(IntPtr client, Guid iface)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (NativeMethods.WlanGetProfileList(client, ref iface, IntPtr.Zero, out var listPtr) != 0) return names;
        try
        {
            var count = Marshal.ReadInt32(listPtr, 0);
            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(listPtr + 8 + i * ProfileInfoSize);
                if (!string.IsNullOrEmpty(name)) names.Add(name);
            }
        }
        finally
        {
            NativeMethods.WlanFreeMemory(listPtr);
        }
        return names;
    }

    internal static List<(Guid Id, string Description)> EnumInterfaces(IntPtr client)
    {
        var list = new List<(Guid, string)>();
        if (NativeMethods.WlanEnumInterfaces(client, IntPtr.Zero, out var ptr) != 0) return list;
        try
        {
            var count = Marshal.ReadInt32(ptr, 0);
            for (var i = 0; i < count; i++)
            {
                var item = ptr + 8 + i * InterfaceInfoSize;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var description = Marshal.PtrToStringUni(item + 16) ?? "";
                list.Add((guid, description));
            }
        }
        finally
        {
            NativeMethods.WlanFreeMemory(ptr);
        }
        return list;
    }
}
