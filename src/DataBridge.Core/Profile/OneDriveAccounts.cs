using Microsoft.Win32;
using DataBridge.Core.IO;

namespace DataBridge.Core.Profile;

public enum OneDriveAccountType { Personal, Business, Unknown }

/// <summary>
/// Non-secret values of one OneDrive account key (HKCU\Software\Microsoft\OneDrive\Accounts\&lt;KeyName&gt;).
/// Only a whitelist of identification values is ever read: no token, cookie or credential.
/// </summary>
public sealed record OneDriveAccountKey(
    string KeyName,
    string? UserFolder,
    string? UserEmail,
    string? DisplayName,
    string? UserName,
    bool? BusinessFlag,
    IReadOnlyList<string> LibraryRoots);

/// <summary>A OneDrive account configured for the current Windows user, identified from local metadata only.</summary>
public sealed record OneDriveAccount(
    string AccountId,
    OneDriveAccountType AccountType,
    string? UserFolder,
    IReadOnlyList<string> SyncRoots,
    string? Email,
    string? DisplayName,
    string? TenantDisplayName)
{
    public const string Unidentified = "Compte non identifié";

    /// <summary>"OneDrive · Personnel", "OneDrive · itelium" or just "OneDrive" when the organisation is unknown.</summary>
    public string Title => AccountType switch
    {
        OneDriveAccountType.Personal => "OneDrive · Personnel",
        _ when !string.IsNullOrWhiteSpace(TenantDisplayName) => $"OneDrive · {TenantDisplayName}",
        _ => "OneDrive",
    };

    /// <summary>Second line: the e-mail, or "Compte non identifié" when nothing else identifies the account.</summary>
    public string? Detail => !string.IsNullOrWhiteSpace(Email) ? Email : Title == "OneDrive" ? Unidentified : null;

    /// <summary>Short name for the log (never the e-mail): "Personnel", the organisation, or the account type.</summary>
    public string LogName => AccountType switch
    {
        OneDriveAccountType.Personal => "Personnel",
        _ when !string.IsNullOrWhiteSpace(TenantDisplayName) => TenantDisplayName!,
        OneDriveAccountType.Business => "professionnel (organisation non indiquée)",
        _ => "type inconnu",
    };

    /// <summary>Compact identification for small spaces: the e-mail, else "Compte Personnel" / "Compte itelium", else "Compte non identifié".</summary>
    public string ShortLabel => !string.IsNullOrWhiteSpace(Email) ? Email!
        : AccountType == OneDriveAccountType.Personal ? "Compte Personnel"
        : !string.IsNullOrWhiteSpace(TenantDisplayName) ? $"Compte {TenantDisplayName}"
        : Unidentified;

    public string TypeLabel => AccountType switch
    {
        OneDriveAccountType.Personal => "Personnel",
        OneDriveAccountType.Business => "Professionnel",
        _ => "Inconnu",
    };
}

/// <summary>A OneDrive sync root (account folder or synced SharePoint library) and the account it belongs to (null = not identified).</summary>
public sealed record OneDriveRoot(string Path, OneDriveAccount? Account)
{
    public string Label => Account?.Title ?? "OneDrive";
    public bool IsBusiness => Account?.AccountType == OneDriveAccountType.Business;
    public string Title => Account?.Title ?? "OneDrive";
    public string Detail => Account is null ? OneDriveAccount.Unidentified : Account.Detail ?? "";
}

/// <summary>Accounts, their roots, and account keys that are present but not configured (no local folder).</summary>
public sealed record OneDriveDetection(IReadOnlyList<OneDriveAccount> Accounts, IReadOnlyList<OneDriveRoot> Roots, IReadOnlyList<string> IgnoredKeys)
{
    public static readonly OneDriveDetection Empty = new([], [], []);
}

/// <summary>
/// Identifies the OneDrive accounts of the current user from local, non-privileged sources only (HKCU registry of the
/// OneDrive client and its environment variables): no Microsoft Graph, no sign-in, nothing sent on the network.
/// A path is associated with the account whose local root contains it, choosing the most specific (longest) root;
/// when nothing reliable is found the account stays "non identifié" rather than guessed.
/// </summary>
public static class OneDriveAccountService
{
    private const string AccountsKey = @"Software\Microsoft\OneDrive\Accounts";

    /// <summary>Reads the registry and the environment of the current session.</summary>
    public static OneDriveDetection Detect()
    {
        IReadOnlyList<OneDriveAccountKey> keys;
        try
        {
            keys = ReadRegistry();
        }
        catch
        {
            keys = [];
        }
        var env = new Dictionary<string, string?>
        {
            ["OneDriveCommercial"] = Environment.GetEnvironmentVariable("OneDriveCommercial"),
            ["OneDriveConsumer"] = Environment.GetEnvironmentVariable("OneDriveConsumer"),
            ["OneDrive"] = Environment.GetEnvironmentVariable("OneDrive"),
        };
        return Build(keys, env.Values);
    }

    /// <summary>Pure part (unit-tested): builds accounts and roots from raw keys and environment roots.</summary>
    public static OneDriveDetection Build(IEnumerable<OneDriveAccountKey> keys, IEnumerable<string?> environmentRoots)
    {
        var accounts = new List<OneDriveAccount>();
        var ignored = new List<string>();
        foreach (var key in keys)
        {
            var folder = NormalizeOrNull(key.UserFolder);
            var roots = new List<string>();
            if (folder != null) roots.Add(folder);
            foreach (var lib in key.LibraryRoots)
                if (NormalizeOrNull(lib) is { } l && !roots.Any(r => PathUtil.AreSame(r, l))) roots.Add(l);
            if (roots.Count == 0)
            {
                ignored.Add(key.KeyName); // key present but no local folder: account not configured on this PC
                continue;
            }

            var type = key.KeyName.Equals("Personal", StringComparison.OrdinalIgnoreCase) ? OneDriveAccountType.Personal
                : key.KeyName.StartsWith("Business", StringComparison.OrdinalIgnoreCase) ? OneDriveAccountType.Business
                : key.BusinessFlag switch { true => OneDriveAccountType.Business, false => OneDriveAccountType.Personal, _ => OneDriveAccountType.Unknown };

            accounts.Add(new OneDriveAccount(
                key.KeyName,
                type,
                folder,
                roots,
                Clean(key.UserEmail),
                Clean(key.UserName),
                // The organisation name is only taken from the client's own value, never derived from a folder name.
                type == OneDriveAccountType.Personal ? null : Clean(key.DisplayName)));
        }

        // One root per path. A path claimed by two different accounts is ambiguous: kept, but unidentified.
        var byPath = new List<OneDriveRoot>();
        foreach (var account in accounts)
        {
            foreach (var root in account.SyncRoots)
            {
                var existing = byPath.FindIndex(r => PathUtil.AreSame(r.Path, root));
                if (existing < 0) byPath.Add(new OneDriveRoot(root, account));
                else if (byPath[existing].Account != account) byPath[existing] = byPath[existing] with { Account = null };
            }
        }

        // Environment roots without a registry account (client signed out, other version): OneDrive, account not identified.
        foreach (var env in environmentRoots)
        {
            if (NormalizeOrNull(env) is { } e && !byPath.Any(r => PathUtil.AreSame(r.Path, e)))
                byPath.Add(new OneDriveRoot(e, null));
        }

        return new OneDriveDetection(accounts, byPath, ignored);
    }

    /// <summary>The root containing <paramref name="path"/>, choosing the most specific (longest) one; null when the path is not in OneDrive.</summary>
    public static OneDriveRoot? Match(string path, IEnumerable<OneDriveRoot> roots)
    {
        if (NormalizeOrNull(path) is not { } p) return null;
        return roots.Where(r => PathUtil.IsSameOrUnder(p, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
    }

    /// <summary>Windows path comparison form: no "\\?\" prefix, backslashes, full path, no trailing separator, variables expanded.</summary>
    public static string? NormalizeOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var p = Environment.ExpandEnvironmentVariables(path.Trim()).Replace('/', '\\');
            p = PathIdentity.StripDevicePrefix(p);
            if (!Path.IsPathFullyQualified(p)) return null;
            return PathUtil.Normalize(p);
        }
        catch
        {
            return null;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ------------------------------------------------------------------ registry (HKCU only, whitelisted values)

    private static IReadOnlyList<OneDriveAccountKey> ReadRegistry()
    {
        var list = new List<OneDriveAccountKey>();
        using var accounts = Registry.CurrentUser.OpenSubKey(AccountsKey);
        if (accounts is null) return list;

        foreach (var name in accounts.GetSubKeyNames())
        {
            try
            {
                using var key = accounts.OpenSubKey(name);
                if (key is null) continue;
                list.Add(new OneDriveAccountKey(
                    name,
                    ReadString(key, "UserFolder"),
                    ReadString(key, "UserEmail"),
                    ReadString(key, "DisplayName"),
                    ReadString(key, "UserName"),
                    key.GetValue("Business") is int b ? b != 0 : null,
                    ReadLibraryRoots(key)));
            }
            catch
            {
                // One unreadable account never prevents detecting the others.
            }
        }
        return list;
    }

    private static string? ReadString(RegistryKey key, string name) => key.GetValue(name) as string;

    /// <summary>
    /// Local folders of synced SharePoint libraries: value names under Tenants\&lt;tenant&gt; and data of
    /// ScopeIdToMountPointPathCache, when the installed client writes them. Only fully qualified paths are kept.
    /// </summary>
    private static IReadOnlyList<string> ReadLibraryRoots(RegistryKey account)
    {
        var roots = new List<string>();
        try
        {
            using var tenants = account.OpenSubKey("Tenants");
            if (tenants != null)
            {
                foreach (var tenant in tenants.GetSubKeyNames())
                {
                    using var t = tenants.OpenSubKey(tenant);
                    if (t is null) continue;
                    roots.AddRange(t.GetValueNames().Where(IsLocalPath));
                }
            }
            using var cache = account.OpenSubKey("ScopeIdToMountPointPathCache");
            if (cache != null)
                roots.AddRange(cache.GetValueNames().Select(v => cache.GetValue(v) as string).Where(IsLocalPath)!);
        }
        catch
        {
            // Optional information.
        }
        return roots;
    }

    private static bool IsLocalPath(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Length > 3 && s[1] == ':' && s[2] == '\\' && char.IsAsciiLetter(s[0]);
}
