using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DataBridge.Core.Native;

namespace DataBridge.Core.Detection;

/// <summary>
/// A network drive of the session: its letter, the share it points to and the account name it was connected with.
/// The password is never readable (Windows keeps it in the Credential Manager): it is never part of the backup.
/// </summary>
/// <param name="Letter">"Z:".</param>
/// <param name="UserName">Account given when the drive was connected ("DOMAINE\compte"); null = the session's own credentials.</param>
/// <param name="Persistent">Reconnected at logon (HKCU\Network), as opposed to a connection for the current session only.</param>
/// <param name="Connected">Currently connected in this session.</param>
public sealed record NetworkDriveInfo(string Letter, string RemotePath, string? UserName, bool Persistent, bool Connected)
{
    public string AccountLabel => UserName ?? "Identifiants de la session Windows";
    public string StateLabel => (Persistent ? "Reconnecté à l'ouverture de session" : "Session en cours uniquement")
                                + (Connected ? "" : " · actuellement déconnecté");
}

/// <summary>Drive letters already used on this PC (restore): network letter → share, local letter → null.</summary>
public sealed record NetworkDriveEnvironment(IReadOnlyDictionary<string, string?> UsedLetters)
{
    public static readonly NetworkDriveEnvironment Empty = new(new Dictionary<string, string?>());

    /// <summary>The letter already points to this share on this PC.</summary>
    public bool IsAlreadyMapped(string letter, string remotePath) =>
        UsedLetters.TryGetValue(letter, out var current) && current != null && NetworkDriveService.SameShare(current, remotePath);

    /// <summary>The letter is taken by a local disk or by another share.</summary>
    public string? Conflict(string letter, string remotePath) =>
        !UsedLetters.TryGetValue(letter, out var current) ? null
        : current is null ? $"lettre {letter} déjà utilisée par un disque de ce PC"
        : current.Length == 0 ? $"lettre {letter} déjà utilisée par un lecteur réseau"
        : NetworkDriveService.SameShare(current, remotePath) ? null
        : $"lettre {letter} déjà connectée à {current}";
}

/// <summary>
/// Network drives through the standard Windows API (mpr.dll) and the session's registry, with the session rights only.
/// Persistent drives are read from HKCU\Network (letter, share, account name), the drives connected for the current
/// session only from WNetGetConnection / WNetGetUser. No password is ever read, displayed or logged.
/// </summary>
public static class NetworkDriveService
{
    private const int NO_ERROR = 0;
    private const int ERROR_MORE_DATA = 234;

    private static readonly Regex LetterPattern = new("^[A-Za-z]:$", RegexOptions.Compiled);

    public static IReadOnlyList<NetworkDriveInfo> Detect()
    {
        var drives = new Dictionary<string, NetworkDriveInfo>(StringComparer.OrdinalIgnoreCase);

        // 1. Persistent drives: what Windows reconnects at logon (also visible when DataBridge runs elevated).
        foreach (var (letter, remote, user) in ReadPersistent())
            drives[letter] = new NetworkDriveInfo(letter, remote, user, Persistent: true, Connected: false);

        // 2. Drives connected now (persistent ones are marked connected; the others exist for this session only).
        foreach (var letter in ConnectedLetters())
        {
            if (GetConnection(letter) is not { } remote) continue;
            drives[letter] = drives.TryGetValue(letter, out var known)
                ? known with { Connected = true }
                : new NetworkDriveInfo(letter, remote, ConnectionUser(letter), Persistent: false, Connected: true);
        }
        return drives.Values.OrderBy(d => d.Letter, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Letters already used on this PC: local disks, connected shares and remembered (disconnected) shares.</summary>
    public static NetworkDriveEnvironment Inspect()
    {
        var used = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                var letter = d.Name[..2].ToUpperInvariant();
                used[letter] = d.DriveType == DriveType.Network ? GetConnection(letter) ?? "" : null;
            }
        }
        catch
        {
            // drive list unavailable: the restore will report the conflicts Windows returns
        }
        foreach (var (letter, remote, _) in ReadPersistent())
            if (!used.TryGetValue(letter, out var current) || current is "") used[letter] = remote;
        return new NetworkDriveEnvironment(used);
    }

    /// <summary>"Z:" (a letter and a colon), upper case.</summary>
    public static bool IsValidLetter(string? letter) => letter != null && LetterPattern.IsMatch(letter);

    /// <summary>A UNC share (\\serveur\partage...), without control characters.</summary>
    public static bool IsValidRemotePath(string? path) =>
        path is { Length: > 4 and < 1024 } && path.StartsWith(@"\\", StringComparison.Ordinal) && path[2] != '\\'
        && path.IndexOf('\\', 2) > 2 && !path.Any(char.IsControl);

    public static bool SameShare(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(string Letter, string Remote, string? User)> ReadPersistent()
    {
        var list = new List<(string, string, string?)>();
        try
        {
            using var network = Registry.CurrentUser.OpenSubKey("Network");
            if (network is null) return list;
            foreach (var name in network.GetSubKeyNames())
            {
                var letter = name + ":";
                if (!IsValidLetter(letter)) continue;
                using var key = network.OpenSubKey(name);
                if (key?.GetValue("RemotePath") is not string remote || remote.Length == 0) continue;
                var user = key.GetValue("UserName") as string;
                list.Add((letter.ToUpperInvariant(), remote, string.IsNullOrWhiteSpace(user) ? null : user));
            }
        }
        catch
        {
            // HKCU\Network unreadable: only the connected drives are listed
        }
        return list;
    }

    private static IEnumerable<string> ConnectedLetters()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Network).Select(d => d.Name[..2].ToUpperInvariant()).ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static string? GetConnection(string letter)
    {
        var length = 512;
        var sb = new StringBuilder(length);
        var error = NativeMethods.WNetGetConnectionW(letter, sb, ref length);
        if (error == ERROR_MORE_DATA)
        {
            sb = new StringBuilder(length);
            error = NativeMethods.WNetGetConnectionW(letter, sb, ref length);
        }
        return error == NO_ERROR && sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Account used by a connection; null when it is the session's own account (nothing to restore).</summary>
    private static string? ConnectionUser(string letter)
    {
        var length = 256;
        var sb = new StringBuilder(length);
        if (NativeMethods.WNetGetUserW(letter, sb, ref length) != NO_ERROR || sb.Length == 0) return null;
        var user = sb.ToString();
        var self = new[] { Environment.UserName, $@"{Environment.UserDomainName}\{Environment.UserName}" };
        return self.Any(s => string.Equals(s, user, StringComparison.OrdinalIgnoreCase)) ? null : user;
    }
}
