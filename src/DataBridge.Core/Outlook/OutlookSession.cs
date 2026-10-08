using System.Diagnostics;

namespace DataBridge.Core.Outlook;

/// <summary>
/// The single connection of DataBridge to classic Outlook. One STA thread runs every COM call (pages and engines), so
/// no COM object is ever shared between threads. DataBridge never starts nor closes Outlook: it must be open.
/// </summary>
public sealed class OutlookSession : IDisposable
{
    private static readonly Lazy<OutlookSession> SharedSession = new(() => new OutlookSession());
    public static OutlookSession Shared => SharedSession.Value;

    private readonly StaWorker _worker = new();
    private readonly OutlookService _service = new();

    public Task<T> RunAsync<T>(Func<OutlookService, T> call) => _worker.RunAsync(() => call(_service));

    /// <summary>Blocking call, for the copy and restore engines (background threads only, never the UI thread).</summary>
    public T Run<T>(Func<OutlookService, T> call) => RunAsync(call).GetAwaiter().GetResult();

    public static bool IsOutlookRunning()
    {
        var processes = Process.GetProcessesByName("OUTLOOK");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            _worker.RunAction(() => _service.Disconnect()).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Outlook already gone: nothing to release.
        }
        _worker.Dispose();
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> (the engines report from their own thread, no context to post to).</summary>
public sealed class Reporter<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
