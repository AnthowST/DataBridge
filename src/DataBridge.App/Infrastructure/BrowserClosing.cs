using System.Windows;
using DataBridge.Core.Detection;

namespace DataBridge.App.Infrastructure;

/// <summary>
/// Restore side of the browser closing (same behaviour as the backup): DataBridge modal, explicit confirmation,
/// <see cref="BrowserProcessService"/> closes the current session's processes with their process tree (never
/// msedgewebview2, never elevated), verification, and a retry dialog when something is still running.
/// </summary>
public static class BrowserClosing
{
    /// <summary>Timestamped events for the log, or null when the technician cancelled (nothing is restored then).</summary>
    public static async Task<List<string>?> EnsureClosedAsync(IReadOnlyList<BrowserKind> open, bool restore)
    {
        var owner = Application.Current.MainWindow;
        if (!Views.DataBridgeDialog.ConfirmBrowserClose(owner, open, restore)) return null;

        var events = new List<string>();
        string Now() => DateTime.Now.ToString("HH:mm:ss");
        foreach (var kind in open) events.Add($"[{Now()}] {BrowserProcessService.DisplayName(kind)} détecté.");
        events.Add($"[{Now()}] Fermeture forcée confirmée par l'utilisateur.");

        var toClose = open;
        while (true)
        {
            var results = await BrowserProcessService.CloseAsync(toClose);
            foreach (var r in results.Where(r => r.Closed))
                events.Add($"[{Now()}] {r.Name} fermé.");
            var remaining = results.Where(r => !r.Closed).Select(r => r.Kind).ToList();
            if (remaining.Count == 0) break;
            foreach (var r in results.Where(r => !r.Closed))
                events.Add($"[{Now()}] {r.Name} : {r.RemainingProcesses} processus toujours actif(s).");
            if (!Views.DataBridgeDialog.RetryBrowserClose(owner, remaining, restore)) return null;
            toClose = remaining;
        }

        events.Add($"[{Now()}] {(restore ? "Restauration" : "Copie")} des profils navigateur démarrée.");
        return events;
    }
}
