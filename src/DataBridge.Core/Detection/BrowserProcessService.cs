using System.ComponentModel;
using System.Diagnostics;

namespace DataBridge.Core.Detection;

/// <summary>Outcome of closing one browser before its profile is copied.</summary>
public sealed record BrowserCloseResult(BrowserKind Kind, string Name, bool WasRunning, bool Closed, int RemainingProcesses);

/// <summary>
/// Detects and fully closes the current user's own browser processes before their profile folders are copied,
/// so files such as Edge's "Cookies" are not locked. It never elevates and never touches other users: only
/// processes of the current Windows session are considered, and each is ended together with its child processes
/// through the standard <see cref="Process.Kill(bool)"/> API (no external command, no backup privilege).
///
/// Scope is strict: only the browser image names (chrome / msedge / firefox) are matched. Edge's WebView2 runtime
/// (image name msedgewebview2) is a different process name and is never matched, so Teams, Outlook, Widgets and
/// other apps that embed WebView2 are left running.
/// </summary>
public static class BrowserProcessService
{
    /// <summary>Process image name (without ".exe") for a browser. WebView2 (msedgewebview2) is deliberately excluded.</summary>
    public static string ProcessName(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "chrome",
        BrowserKind.Edge => "msedge",
        _ => "firefox",
    };

    public static string DisplayName(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        _ => "Mozilla Firefox",
    };

    /// <summary>Number of the browser's own processes running in the current session (0 = not open).</summary>
    public static int CountRunning(BrowserKind kind)
    {
        var processes = OwnProcesses(kind);
        try
        {
            return processes.Count;
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    public static bool IsRunning(BrowserKind kind) => CountRunning(kind) > 0;

    /// <summary>
    /// Fully closes the given browsers: each matching process of the current session is ended with its children,
    /// then the service waits and re-checks, repeating a few times over a few seconds. Runs on a background thread.
    /// </summary>
    public static Task<IReadOnlyList<BrowserCloseResult>> CloseAsync(IEnumerable<BrowserKind> kinds) =>
        Task.Run(() => Close(kinds));

    public static IReadOnlyList<BrowserCloseResult> Close(IEnumerable<BrowserKind> kinds)
    {
        var results = new List<BrowserCloseResult>();
        foreach (var kind in kinds.Distinct())
        {
            var before = CountRunning(kind);
            if (before == 0)
            {
                results.Add(new BrowserCloseResult(kind, DisplayName(kind), false, true, 0));
                continue;
            }

            var remaining = before;
            // A few short rounds: kill the tree, wait briefly, re-check. Never blocks for long.
            for (var round = 0; round < 4 && remaining > 0; round++)
            {
                KillOnce(kind);
                WaitForExit(kind, TimeSpan.FromMilliseconds(1200));
                remaining = CountRunning(kind);
            }

            results.Add(new BrowserCloseResult(kind, DisplayName(kind), true, remaining == 0, remaining));
        }
        return results;
    }

    private static void KillOnce(BrowserKind kind)
    {
        var processes = OwnProcesses(kind);
        try
        {
            foreach (var p in processes)
            {
                try
                {
                    // Ends this process together with any child processes it started (renderers, GPU, utility...).
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited between enumeration and kill.
                }
                catch (Win32Exception)
                {
                    // Access denied (e.g. a process we cannot control with session rights): reported as "remaining".
                }
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static void WaitForExit(BrowserKind kind, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        var processes = OwnProcesses(kind);
        try
        {
            foreach (var p in processes)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) break;
                try { p.WaitForExit((int)left.TotalMilliseconds); }
                catch { /* process gone or not waitable: re-checked by CountRunning */ }
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>Live processes of this browser that belong to the current Windows session only.</summary>
    private static List<Process> OwnProcesses(BrowserKind kind)
    {
        var sessionId = CurrentSessionId();
        var result = new List<Process>();
        Process[] all;
        try
        {
            all = Process.GetProcessesByName(ProcessName(kind));
        }
        catch
        {
            return result;
        }

        foreach (var p in all)
        {
            var keep = false;
            try
            {
                keep = !p.HasExited && p.SessionId == sessionId;
            }
            catch
            {
                // Cannot read this process (another user / protected): never ours, never touched.
            }

            if (keep) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    private static int CurrentSessionId()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            return me.SessionId;
        }
        catch
        {
            return -1;
        }
    }
}
