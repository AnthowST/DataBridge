using DataBridge.Core.Detection;
using DataBridge.Core.Native;

namespace DataBridge.Core.Restore;

public enum NetworkDriveRestoreOutcome { Connected, AlreadyPresent, Conflict, NotRestored, Failed }

public sealed record NetworkDriveRestoreResult(string Letter, string RemotePath, NetworkDriveRestoreOutcome Outcome, string Message)
{
    public bool Restored => Outcome == NetworkDriveRestoreOutcome.Connected;
}

/// <summary>
/// Reconnects saved network drives with the standard Windows API and the rights of the session (persistent connection,
/// same letter, same share, same account name). The password was never saved: when the share asks for one, Windows
/// itself shows its credential prompt (account name filled in) if the technician allowed it; the password typed there
/// never goes through DataBridge. A letter already used by something else is never taken over.
/// </summary>
public static class NetworkDriveRestoreService
{
    private const int NO_ERROR = 0;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_BAD_NETPATH = 53;
    private const int ERROR_BAD_NET_NAME = 67;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_INVALID_PASSWORD = 86;
    private const int ERROR_DEVICE_ALREADY_REMEMBERED = 1202;
    private const int ERROR_NO_NET_OR_BAD_PATH = 1203;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_NO_NETWORK = 1222;
    private const int ERROR_CANCELLED = 1223;
    private const int ERROR_NOT_AUTHENTICATED = 1244;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_BAD_USERNAME = 2202;

    /// <param name="ownerWindow">Window owning Windows' credential prompt (IntPtr.Zero: no prompt possible).</param>
    /// <param name="askPassword">When the share requires a password, let Windows ask for it.</param>
    public static NetworkDriveRestoreResult Connect(BackupNetworkDrive drive, NetworkDriveEnvironment environment, IntPtr ownerWindow, bool askPassword)
    {
        NetworkDriveRestoreResult Result(NetworkDriveRestoreOutcome outcome, string message) => new(drive.Letter, drive.RemotePath, outcome, message);

        if (!NetworkDriveService.IsValidLetter(drive.Letter) || !NetworkDriveService.IsValidRemotePath(drive.RemotePath))
            return Result(NetworkDriveRestoreOutcome.Failed, "Lettre ou chemin réseau invalide dans la sauvegarde : non restauré.");
        if (environment.IsAlreadyMapped(drive.Letter, drive.RemotePath))
            return Result(NetworkDriveRestoreOutcome.AlreadyPresent, "Lecteur déjà connecté sur ce PC (même lettre, même partage) : conservé.");
        if (environment.Conflict(drive.Letter, drive.RemotePath) is { } conflict)
            return Result(NetworkDriveRestoreOutcome.Conflict, $"Non restauré : {conflict} (rien n'est modifié).");

        var resource = new NativeMethods.NETRESOURCEW
        {
            dwType = NativeMethods.RESOURCETYPE_DISK,
            lpLocalName = drive.Letter,
            lpRemoteName = drive.RemotePath,
        };

        int error;
        try
        {
            // First without any prompt: the session's credentials, or those already stored in the Credential Manager.
            error = NativeMethods.WNetAddConnection2W(ref resource, null, drive.UserName, NativeMethods.CONNECT_UPDATE_PROFILE);
            if (NeedsCredentials(error) && askPassword && ownerWindow != IntPtr.Zero)
                error = NativeMethods.WNetAddConnection3W(ownerWindow, ref resource, null, drive.UserName,
                    NativeMethods.CONNECT_UPDATE_PROFILE | NativeMethods.CONNECT_INTERACTIVE | NativeMethods.CONNECT_PROMPT);
        }
        catch (Exception ex)
        {
            return Result(NetworkDriveRestoreOutcome.Failed, "Connexion impossible : " + ex.Message);
        }

        return error switch
        {
            NO_ERROR => Result(NetworkDriveRestoreOutcome.Connected,
                "Lecteur connecté (reconnecté à l'ouverture de session)" + (drive.UserName != null ? $" avec le compte {drive.UserName}." : ".")),
            ERROR_CANCELLED => Result(NetworkDriveRestoreOutcome.NotRestored, "Demande du mot de passe annulée : lecteur non connecté."),
            _ when NeedsCredentials(error) => Result(NetworkDriveRestoreOutcome.NotRestored,
                "Le partage demande un mot de passe" + (drive.UserName != null ? $" pour {drive.UserName}" : "") + " : lecteur non connecté (à connecter manuellement)."),
            ERROR_SESSION_CREDENTIAL_CONFLICT => Result(NetworkDriveRestoreOutcome.NotRestored,
                "Ce serveur est déjà connecté avec un autre compte : Windows refuse un second compte vers le même serveur."),
            ERROR_ALREADY_ASSIGNED or ERROR_DEVICE_ALREADY_REMEMBERED => Result(NetworkDriveRestoreOutcome.Conflict,
                $"Non restauré : lettre {drive.Letter} déjà utilisée sur ce PC (rien n'est modifié)."),
            ERROR_BAD_NETPATH or ERROR_BAD_NET_NAME or ERROR_NO_NET_OR_BAD_PATH or ERROR_NO_NETWORK => Result(NetworkDriveRestoreOutcome.Failed,
                "Partage inaccessible depuis ce PC (serveur introuvable, réseau ou VPN absent) : lecteur non connecté."),
            _ => Result(NetworkDriveRestoreOutcome.Failed, $"Connexion refusée par Windows : {new System.ComponentModel.Win32Exception(error).Message} (code {error})."),
        };
    }

    private static bool NeedsCredentials(int error) =>
        error is ERROR_ACCESS_DENIED or ERROR_INVALID_PASSWORD or ERROR_LOGON_FAILURE or ERROR_NOT_AUTHENTICATED or ERROR_BAD_USERNAME;
}
