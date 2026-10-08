using System.ComponentModel;
using System.Runtime.InteropServices;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Native;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.UserScope;

/// <summary>
/// Operations that belong to the current user's profile. They always run in the non-elevated process of the
/// session user, never in the elevated worker (which may run under another administrator account).
/// </summary>
public static class UserOps
{
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_PRINTER_DRIVER_DOWNLOAD_NEEDED = 3019;
    public const int ERROR_PRINTER_NOT_FOUND = 3012;
    public const int ERROR_BAD_NETPATH = 53;

    public static OpResult AddConnection(string uncPath)
    {
        var info = new PRINTER_CONNECTION_INFO_1 { dwFlags = PRINTER_CONNECTION_NO_UI };
        if (AddPrinterConnection2(IntPtr.Zero, uncPath, 1, ref info))
            return OpResult.Success($"Connexion à « {uncPath} » créée.");
        int err = Marshal.GetLastWin32Error();
        string hint = err switch
        {
            ERROR_PRINTER_DRIVER_DOWNLOAD_NEEDED or ERROR_ACCESS_DENIED =>
                " Le pilote du serveur doit être installé par un administrateur (règles Point and Print de Windows). Installez le pilote depuis l'archive, puis réessayez.",
            ERROR_BAD_NETPATH or ERROR_INVALID_PRINTER_NAME or ERROR_PRINTER_NOT_FOUND =>
                " Le serveur ou le partage est introuvable ou injoignable.",
            _ => "",
        };
        return OpResult.Fail($"Connexion à « {uncPath} » impossible : {new Win32Exception(err).Message}.{hint}");
    }

    public static OpResult DeleteConnection(string uncPath)
    {
        if (DeletePrinterConnection(uncPath)) return OpResult.Success($"Connexion « {uncPath} » supprimée.");
        return OpResult.Fail($"Suppression de la connexion « {uncPath} » impossible : {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    public static OpResult SetUserDevMode(string printerName, byte[] devMode)
    {
        try
        {
            using var h = PrinterHandle.Open(printerName, PRINTER_ACCESS_USE);
            Spooler.SetDevModeLevel(h, 9, devMode);
            return OpResult.Success("Préférences de l'utilisateur écrites.");
        }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    public static OpResult SetDefault(string printerName)
    {
        if (!SetDefaultPrinter(printerName))
            return OpResult.Fail($"Impossible de définir « {printerName} » par défaut : {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        string? now = SystemInfo.DefaultPrinter();
        return string.Equals(now, printerName, StringComparison.OrdinalIgnoreCase)
            ? OpResult.Success($"« {printerName} » est l'imprimante par défaut.")
            : OpResult.Fail($"Windows indique « {now} » comme imprimante par défaut après la modification.");
    }
}
