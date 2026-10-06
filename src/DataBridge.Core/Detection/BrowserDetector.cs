using System.Diagnostics;
using System.Text.Json;
using DataBridge.Core.IO;

namespace DataBridge.Core.Detection;

public enum BrowserKind { Chrome, Edge, Firefox }

public sealed record BrowserProfileInfo(
    string FullPath,
    string FolderName,
    string DisplayName,
    string? Account,
    bool IsActive,
    bool PreSelected);

public sealed record BrowserInfo(
    BrowserKind Kind,
    string Name,
    string ProcessName,
    string DataRoot,
    bool Detected,
    IReadOnlyList<BrowserProfileInfo> Profiles,
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> ExcludedSubfolders,
    string? ResolutionMethod,
    string? Warning);

/// <summary>
/// Detects Chrome, Edge and Firefox data of the current user. Profiles are copied as files only:
/// nothing is decrypted, and saved passwords are never read or exported.
/// </summary>
public static class BrowserDetector
{
    /// <summary>Cache folders skipped inside Chromium profiles (rebuilt automatically by the browser).</summary>
    public static readonly IReadOnlyList<string> ChromiumCacheFolders =
    [
        "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GrShaderCache",
        "ShaderCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache", "Media Cache", "Application Cache",
    ];

    /// <summary>Cache / telemetry folders skipped inside Firefox profiles.</summary>
    public static readonly IReadOnlyList<string> FirefoxCacheFolders =
    [
        "cache2", "startupCache", "thumbnails", "shader-cache", "crashes", "minidumps", "saved-telemetry-pings", @"datareporting\archived",
    ];

    public static IReadOnlyList<BrowserInfo> DetectAll(string localAppData, string roamingAppData) =>
    [
        DetectChromium(BrowserKind.Chrome, "Google Chrome", "chrome", Path.Combine(localAppData, @"Google\Chrome\User Data")),
        DetectChromium(BrowserKind.Edge, "Microsoft Edge", "msedge", Path.Combine(localAppData, @"Microsoft\Edge\User Data")),
        DetectFirefox(Path.Combine(roamingAppData, @"Mozilla\Firefox")),
    ];

    public static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    public static BrowserInfo DetectChromium(BrowserKind kind, string name, string processName, string userData)
    {
        if (!Directory.Exists(userData))
            return new BrowserInfo(kind, name, processName, userData, false, [], [], ChromiumCacheFolders, null, null);

        var infoCache = new Dictionary<string, (string Name, string? Account)>(StringComparer.OrdinalIgnoreCase);
        string? lastUsed = null;
        try
        {
            var localState = Path.Combine(userData, "Local State");
            if (File.Exists(localState))
            {
                using var stream = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("profile", out var profile))
                {
                    if (profile.TryGetProperty("last_used", out var lu) && lu.ValueKind == JsonValueKind.String) lastUsed = lu.GetString();
                    if (profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in cache.EnumerateObject())
                        {
                            var display = p.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                            var account = p.Value.TryGetProperty("user_name", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                            infoCache[p.Name] = (display ?? p.Name, string.IsNullOrWhiteSpace(account) ? null : account);
                        }
                    }
                }
            }
        }
        catch
        {
            // Local State unreadable: fall back to folder names.
        }

        var folders = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(userData))
            {
                var folder = Path.GetFileName(dir);
                if (folder.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    || folder.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                    || infoCache.ContainsKey(folder))
                {
                    if (File.Exists(Path.Combine(dir, "Preferences")) || infoCache.ContainsKey(folder)) folders.Add(folder);
                }
            }
        }
        catch
        {
            // ignore
        }

        var profiles = folders
            .OrderBy(f => f.Equals("Default", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var (display, account) = infoCache.TryGetValue(f, out var info) ? info : (f, null);
                var active = lastUsed != null ? f.Equals(lastUsed, StringComparison.OrdinalIgnoreCase) : f.Equals("Default", StringComparison.OrdinalIgnoreCase);
                // Every Chromium profile belongs to this Windows user: all are preselected.
                return new BrowserProfileInfo(PathUtil.Normalize(Path.Combine(userData, f)), f, display, account, active, true);
            })
            .ToList();

        return new BrowserInfo(kind, name, processName, PathUtil.Normalize(userData), true, profiles, ["Local State"], ChromiumCacheFolders,
            "Local State (profile.info_cache)", profiles.Count == 0 ? "Aucun profil trouvé dans User Data." : null);
    }

    public static BrowserInfo DetectFirefox(string firefoxRoot)
    {
        var profilesDir = Path.Combine(firefoxRoot, "Profiles");
        if (!Directory.Exists(firefoxRoot) || (!Directory.Exists(profilesDir) && !File.Exists(Path.Combine(firefoxRoot, "profiles.ini"))))
            return new BrowserInfo(BrowserKind.Firefox, "Mozilla Firefox", "firefox", profilesDir, false, [], [], FirefoxCacheFolders, null, null);

        string? Read(string file)
        {
            try
            {
                var path = Path.Combine(firefoxRoot, file);
                if (!File.Exists(path)) return null;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch
            {
                return null;
            }
        }

        var resolution = FirefoxProfiles.Resolve(PathUtil.Normalize(firefoxRoot), Read("profiles.ini"), Read("installs.ini"));
        var profiles = resolution.Profiles
            .Where(p => p.Exists)
            .Select(p =>
            {
                var active = resolution.ActiveProfilePaths.Any(a => PathUtil.AreSame(a, p.FullPath));
                return new BrowserProfileInfo(p.FullPath, PathUtil.DisplayName(p.FullPath), p.Name, null, active, active);
            })
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rootFiles = new[] { "profiles.ini", "installs.ini" }.Where(f => File.Exists(Path.Combine(firefoxRoot, f))).ToList();
        return new BrowserInfo(BrowserKind.Firefox, "Mozilla Firefox", "firefox", PathUtil.Normalize(firefoxRoot), true, profiles, rootFiles,
            FirefoxCacheFolders, resolution.Method, resolution.Warning);
    }
}
