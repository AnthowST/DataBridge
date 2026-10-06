using DataBridge.Core.IO;

namespace DataBridge.Core.Detection;

public sealed record FirefoxProfileEntry(string Name, string FullPath, bool DefaultFlag, bool Exists);

/// <summary>Outcome of reading profiles.ini / installs.ini.</summary>
public sealed record FirefoxResolution(
    IReadOnlyList<FirefoxProfileEntry> Profiles,
    IReadOnlyList<string> ActiveProfilePaths,
    string Method,
    string? Warning);

/// <summary>
/// Determines the active Firefox profile(s) from profiles.ini and installs.ini.
/// Order of trust: per-installation defaults ([Install...] sections, installs.ini),
/// then the legacy Default=1 flag, then "only one profile exists". Otherwise nothing is chosen.
/// </summary>
public static class FirefoxProfiles
{
    public static Dictionary<string, Dictionary<string, string>> ParseIni(string? text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return result;
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!result.TryGetValue(name, out current))
                {
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[name] = current;
                }
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    public static FirefoxResolution Resolve(string firefoxRoot, string? profilesIni, string? installsIni, Func<string, bool>? directoryExists = null)
    {
        directoryExists ??= Directory.Exists;
        var profilesSections = ParseIni(profilesIni);
        var installsSections = ParseIni(installsIni);

        string ToFull(string path, bool relative) =>
            PathUtil.Normalize(relative ? Path.Combine(firefoxRoot, path.Replace('/', '\\')) : path.Replace('/', '\\'));

        var profiles = new List<FirefoxProfileEntry>();
        foreach (var (section, values) in profilesSections)
        {
            if (!section.StartsWith("Profile", StringComparison.OrdinalIgnoreCase)) continue;
            if (!values.TryGetValue("Path", out var p) || string.IsNullOrWhiteSpace(p)) continue;
            var relative = !values.TryGetValue("IsRelative", out var rel) || rel == "1";
            var full = ToFull(p, relative);
            if (profiles.Any(x => PathUtil.AreSame(x.FullPath, full))) continue;
            var name = values.TryGetValue("Name", out var n) && n.Length > 0 ? n : PathUtil.DisplayName(full);
            profiles.Add(new FirefoxProfileEntry(name, full, values.TryGetValue("Default", out var d) && d == "1", directoryExists(full)));
        }

        // Per-installation defaults (Firefox 67+): [InstallXXXX] in profiles.ini, [XXXX] in installs.ini.
        var installDefaults = new List<string>();
        void AddInstallDefault(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            // Install defaults are relative to the Firefox root unless absolute.
            var full = Path.IsPathRooted(value) ? ToFull(value, false) : ToFull(value, true);
            if (!installDefaults.Any(x => PathUtil.AreSame(x, full))) installDefaults.Add(full);
        }
        foreach (var (section, values) in profilesSections)
            if (section.StartsWith("Install", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("Default", out var v))
                AddInstallDefault(v);
        foreach (var (_, values) in installsSections)
            if (values.TryGetValue("Default", out var v))
                AddInstallDefault(v);

        // Profiles present on disk but referenced only by an install section.
        foreach (var path in installDefaults.Where(p => profiles.All(x => !PathUtil.AreSame(x.FullPath, p))))
            profiles.Add(new FirefoxProfileEntry(PathUtil.DisplayName(path), path, false, directoryExists(path)));

        var existingInstallDefaults = installDefaults.Where(directoryExists).ToList();
        if (existingInstallDefaults.Count > 0)
        {
            var method = existingInstallDefaults.Count == 1
                ? "Profil par défaut de l'installation (installs.ini / profiles.ini)"
                : "Profils par défaut de chaque installation de Firefox (installs.ini / profiles.ini)";
            return new FirefoxResolution(profiles, existingInstallDefaults, method, null);
        }

        var flagged = profiles.Where(p => p.DefaultFlag && p.Exists).ToList();
        if (flagged.Count == 1)
            return new FirefoxResolution(profiles, [flagged[0].FullPath], "Profil marqué Default=1 (profiles.ini)", null);

        var existing = profiles.Where(p => p.Exists).ToList();
        if (existing.Count == 1)
            return new FirefoxResolution(profiles, [existing[0].FullPath], "Profil unique", null);

        if (existing.Count == 0)
            return new FirefoxResolution(profiles, [], "Aucun profil", null);

        return new FirefoxResolution(profiles, [], "Indéterminé",
            "Plusieurs profils Firefox existent et le profil actif ne peut pas être identifié avec certitude. " +
            "Aucun profil n'a été présélectionné : cochez manuellement le ou les profils à sauvegarder.");
    }
}
