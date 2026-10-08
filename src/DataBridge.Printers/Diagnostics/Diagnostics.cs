using System.Net.Sockets;
using DataBridge.Printers.Inventory;
using DataBridge.Printers.Native;
using static DataBridge.Printers.Native.NativeMethods;

namespace DataBridge.Printers.Diagnostics;

/// <summary>DataBridge never sends a test page or any print job: only the network / share reachability is checked.</summary>
public sealed record ProbeResult(bool? Success, string Detail);

public static class Reachability
{
    public static async Task<ProbeResult> TcpAsync(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cts.Token);
            return new ProbeResult(true, $"{host}:{port} répond.");
        }
        catch (OperationCanceledException) { return new ProbeResult(false, $"{host}:{port} ne répond pas (délai dépassé)."); }
        catch (Exception ex) { return new ProbeResult(false, $"{host}:{port} injoignable : {ex.Message}"); }
    }

    /// <summary>Opens the queue through the spooler: proves the share is reachable and the user is allowed to use it.</summary>
    public static ProbeResult ShareAccess(string uncPath)
    {
        var task = Task.Run(() =>
        {
            if (PrinterHandle.TryOpen(uncPath, PRINTER_ACCESS_USE, out var h, out int err))
            {
                h!.Dispose();
                return new ProbeResult(true, "Accès au partage autorisé.");
            }
            return new ProbeResult(false, $"Accès au partage refusé ou impossible : {Spooler.WindowsErrorText(err)}");
        });
        return task.Wait(TimeSpan.FromSeconds(20)) ? task.Result : new ProbeResult(false, "Le serveur ne répond pas (délai dépassé).");
    }
}
