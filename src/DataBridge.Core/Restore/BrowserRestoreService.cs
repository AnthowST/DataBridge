using System.Text;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;

namespace DataBridge.Core.Restore;

/// <summary>A profile of this PC (or a profile DataBridge will create) that a saved profile can be restored into.</summary>
public sealed record BrowserTargetOption(string Key, string Label, string Detail, string ProfilePath, bool CreatesProfile, bool IsActive)
{
    public override string ToString() => Label;
}

/// <summary>
/// Restore strategy for browser profiles. It is not a blind copy of the old "User Data" folder:
/// <list type="bullet">
/// <item>Chrome / Edge: each profile folder is restored into a profile chosen on this PC, and the rest of the saved
/// "User Data" folder (browser-wide data next to the profiles) into this PC's "User Data". The "Local State" file of
/// this PC is never replaced: it holds the encryption key protected by Windows (DPAPI) for this account. Files that can
/// only be read with the old PC's key (saved passwords, cookies, autofill tokens and cards, sync and push
/// registrations) are not restored: copying them would bring unreadable data and could break sign-in.</item>
/// <item>Firefox: the profile folder is restored into an existing profile, or into a new profile registered in
/// profiles.ini (the existing installation and its other profiles are left as they are).</item>
/// <item>Before a file of an existing profile is replaced, it is copied to %LOCALAPPDATA%\DataBridge\RestoreSafety.</item>
/// </list>
/// Passwords, cookies and sessions are never guaranteed on another PC.
/// </summary>
public static class BrowserRestoreService
{
    /// <summary>Chromium profile files bound to the old PC / Windows account (DPAPI or device registration): never restored.</summary>
    public static readonly IReadOnlyList<string> ChromiumNotRestored =
    [
        "Login Data", "Login Data-journal", "Login Data For Account", "Login Data For Account-journal",
        "Cookies", "Cookies-journal", @"Network\Cookies", @"Network\Cookies-journal",
        "Web Data", "Web Data-journal", "Account Web Data", "Account Web Data-journal",
        "Trust Tokens", "Trust Tokens-journal", @"Network\Trust Tokens", @"Network\Trust Tokens-journal",
        "Sync Data", "GCM Store", "LOCK", "lockfile",
    ];

    /// <summary>Firefox lock files of the old session.</summary>
    public static readonly IReadOnlyList<string> FirefoxNotRestored = ["parent.lock", ".parentlock", "lock"];

    /// <summary>
    /// Files at the root of "User Data" that belong to this PC's installation: never restored with the common data
    /// (Local State holds this account's DPAPI key and the profile list of this PC).
    /// </summary>
    public static readonly IReadOnlyList<string> ChromiumUserDataNotRestored = ["Local State", "lockfile", "Last Version", "Last Browser"];

    public static IReadOnlyList<string> NotRestored(BrowserKind kind) => kind == BrowserKind.Firefox ? FirefoxNotRestored : ChromiumNotRestored;

    public static string CommonDataLabel(string browserName) => $"{browserName} — données communes (User Data)";

    /// <summary>
    /// Exclusions of the "common data" item of a saved Chrome / Edge "User Data" folder: the files of this installation
    /// and every profile folder (each profile is restored on its own, into the profile chosen for it). Null when the
    /// backup holds nothing else (1.x backups: Local State and the profiles only) or no "User Data" folder.
    /// </summary>
    public static IReadOnlyList<string>? CommonDataExclusions(BackupBrowser browser)
    {
        if (browser.Kind == BrowserKind.Firefox) return null;
        if (!Path.GetFileName(browser.FullPath).Equals("User Data", StringComparison.OrdinalIgnoreCase)) return null;
        if (FastDirectory.GetEntry(browser.FullPath) is not { IsDirectory: true, IsLink: false }) return null;
        if (FastDirectory.TryList(browser.FullPath, out _) is not { } entries) return null;

        var excluded = new List<string>(ChromiumUserDataNotRestored);
        var hasOther = false;
        foreach (var e in entries)
        {
            var isProfile = e.IsDirectory && (e.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || e.Name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)
                || browser.Profiles.Any(p => PathUtil.AreSame(p.FullPath, e.FullPath)));
            if (isProfile) excluded.Add(e.Name);
            else if (!ChromiumUserDataNotRestored.Contains(e.Name, StringComparer.OrdinalIgnoreCase)) hasOther = true;
        }
        return hasOther ? excluded : null;
    }

    /// <summary>Plain-language description for the summary, the log and the report.</summary>
    public static IReadOnlyList<string> Description(BrowserKind kind) => kind == BrowserKind.Firefox
        ?
        [
            "Restauré : favoris, historique, extensions, préférences, sessions, données de sites (dossier du profil, caches exclus).",
            "Le profil est restauré dans un profil Firefox de ce PC, ou dans un nouveau profil enregistré dans profiles.ini.",
            "Non garantis : mots de passe, cookies et sessions ouvertes (protégés, liés au PC d'origine).",
        ]
        :
        [
            "Restauré : favoris, historique, extensions, préférences, onglets, données de sites (dossier du profil, caches exclus).",
            "Restauré aussi : le reste du dossier « User Data » (données communes du navigateur, à côté des profils).",
            "Non restauré : « Local State » de ce PC conservé (clé de chiffrement Windows de ce compte).",
            "Non restauré : mots de passe (Login Data), cookies, jetons et cartes (Web Data), données de synchronisation (Sync Data, GCM Store).",
            "Le navigateur peut redemander la connexion au compte et réinitialiser certains paramètres protégés.",
        ];

    // ------------------------------------------------------------------ targets

    public static string FirefoxRoot(RestoreTarget target) => Path.Combine(target.RoamingAppData, @"Mozilla\Firefox");

    public static string ChromiumUserData(RestoreTarget target, BrowserKind kind) => kind == BrowserKind.Edge
        ? Path.Combine(target.LocalAppData, @"Microsoft\Edge\User Data")
        : Path.Combine(target.LocalAppData, @"Google\Chrome\User Data");

    /// <summary>
    /// Profiles of this PC a saved profile can go to. For Firefox, <paramref name="newFirefoxFolder"/> is the folder of
    /// the profile DataBridge would create (chosen once per selection so the analysis and the restore agree).
    /// </summary>
    public static IReadOnlyList<BrowserTargetOption> TargetOptions(BrowserKind kind, RestoreTarget target, BackupBrowserProfile source, string? newFirefoxFolder)
    {
        var options = new List<BrowserTargetOption>();
        var info = target.Browser(kind);
        var existing = info is { Detected: true } ? info.Profiles : [];
        foreach (var p in existing)
        {
            var detail = string.Join(" · ", new[] { p.FolderName, p.Account }.Where(s => !string.IsNullOrWhiteSpace(s)));
            options.Add(new BrowserTargetOption("existing:" + p.FullPath, $"Profil actuel : {p.DisplayName}", detail, p.FullPath, false, p.IsActive));
        }

        if (kind == BrowserKind.Firefox)
        {
            var root = FirefoxRoot(target);
            var folder = newFirefoxFolder ?? NewFirefoxFolderName(source.DisplayName);
            options.Add(new BrowserTargetOption("new", $"Nouveau profil Firefox « {source.DisplayName} »",
                existing.Count == 0 ? "créé par DataBridge et utilisé par défaut" : "créé par DataBridge (à choisir dans about:profiles)",
                Path.Combine(root, "Profiles", folder), true, false));
        }
        else if (existing.Count == 0)
        {
            // Browser never started on this PC: restore into the Default profile it will use on first start.
            options.Add(new BrowserTargetOption("new", "Nouveau profil « Default »",
                "le navigateur n'a pas encore de profil sur ce PC : il l'utilisera au premier démarrage",
                Path.Combine(ChromiumUserData(target, kind), "Default"), true, false));
        }
        return options;
    }

    /// <summary>Automatic choice when it is unambiguous; null when the technician must choose.</summary>
    public static BrowserTargetOption? DefaultOption(BrowserKind kind, IReadOnlyList<BrowserTargetOption> options)
    {
        var existing = options.Where(o => !o.CreatesProfile).ToList();
        if (existing.Count == 1) return existing[0];
        if (existing.Count == 0) return options.FirstOrDefault(o => o.CreatesProfile);
        if (kind == BrowserKind.Firefox && existing.Count(o => o.IsActive) == 1) return existing.First(o => o.IsActive);
        return null; // several profiles: the technician chooses
    }

    public static string NewFirefoxFolderName(string displayName)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        var random = new string(Enumerable.Range(0, 8).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
        var name = new string(PathUtil.SanitizeSegment(displayName, "profil").Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return $"{random}.{(name.Length > 0 ? name : "profil")}-databridge";
    }

    // ------------------------------------------------------------------ Firefox registration

    /// <summary>
    /// Adds the restored profile to profiles.ini (created when Firefox was never started). The previous profiles.ini is
    /// copied to <paramref name="safetyFolder"/> first. Returns a message for the log.
    /// </summary>
    public static string RegisterFirefoxProfile(FirefoxRegistration reg, string? safetyFolder)
    {
        var ini = Path.Combine(reg.FirefoxRoot, "profiles.ini");
        Directory.CreateDirectory(reg.FirefoxRoot);
        var relative = reg.RelativePath.Replace('\\', '/');

        if (!File.Exists(ini))
        {
            var text = new StringBuilder()
                .Append("[General]\r\nStartWithLastProfile=1\r\nVersion=2\r\n\r\n")
                .Append($"[Profile0]\r\nName={reg.ProfileName}\r\nIsRelative=1\r\nPath={relative}\r\nDefault=1\r\n");
            File.WriteAllText(ini, text.ToString(), new UTF8Encoding(false));
            return $"profiles.ini créé avec le profil « {reg.ProfileName} » (profil par défaut).";
        }

        var existing = File.ReadAllText(ini);
        var sections = FirefoxProfiles.ParseIni(existing);
        if (sections.Values.Any(v => v.TryGetValue("Path", out var p) && p.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)))
            return "profil déjà présent dans profiles.ini.";

        if (safetyFolder != null)
        {
            Directory.CreateDirectory(safetyFolder);
            File.Copy(ini, Path.Combine(safetyFolder, "profiles.ini"), overwrite: true);
        }

        var next = 0;
        while (sections.ContainsKey($"Profile{next}")) next++;
        var names = sections.Values.Select(v => v.TryGetValue("Name", out var n) ? n : "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = reg.ProfileName;
        for (var i = 2; names.Contains(name); i++) name = $"{reg.ProfileName} ({i})";

        var block = new StringBuilder();
        if (!existing.EndsWith('\n')) block.Append("\r\n");
        block.Append($"\r\n[Profile{next}]\r\nName={name}\r\nIsRelative=1\r\nPath={relative}\r\n");
        if (reg.MakeDefault) block.Append("Default=1\r\n");
        File.AppendAllText(ini, block.ToString(), new UTF8Encoding(false));
        return $"profil « {name} » ajouté à profiles.ini" + (reg.MakeDefault ? " (profil par défaut)." : " (à choisir dans about:profiles).");
    }
}
