// Moteur Outlook repris de Sauvegarde Outlook (même format de PST et mêmes propriétés de dossier :
// une sauvegarde faite par DataBridge peut aussi être restaurée avec Sauvegarde Outlook, et inversement).
#nullable disable
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DataBridge.Core.Outlook;

/// <summary>
/// Thread STA unique qui exécute tous les appels COM vers Outlook,
/// pour garder l'interface réactive et ne jamais partager d'objet COM entre threads.
/// </summary>
public sealed class StaWorker : IDisposable
{
    readonly BlockingCollection<Action> _queue = new();
    readonly Thread _thread;

    public StaWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Outlook COM" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Loop()
    {
        OleMessageFilter.Register();
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }
        finally
        {
            OleMessageFilter.Revoke();
        }
    }

    public Task<T> RunAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task RunAction(Action action) => RunAsync(() => { action(); return true; });

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>
/// Réessaie automatiquement les appels refusés quand Outlook est occupé (RPC_E_CALL_REJECTED).
/// </summary>
public sealed class OleMessageFilter : IOleMessageFilter
{
    [DllImport("ole32.dll")]
    static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

    public static void Register() => CoRegisterMessageFilter(new OleMessageFilter(), out _);
    public static void Revoke() => CoRegisterMessageFilter(null, out _);

    int IOleMessageFilter.HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => 0;

    int IOleMessageFilter.RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
    {
        // SERVERCALL_RETRYLATER : on réessaie toutes les 250 ms pendant 2 minutes maximum.
        if (dwRejectType == 2 && dwTickCount < 120_000) return 250;
        return -1;
    }

    int IOleMessageFilter.MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => 2;
}

[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
    [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}
