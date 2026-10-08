using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DataBridge.Core.Native;

namespace DataBridge.Core.Detection;

/// <summary>
/// Wi-Fi keys that Windows refuses to the session (2.1.1). The profiles concerned are read again by ONE elevated
/// instance, "DataBridge.exe --wifi-key-worker &lt;pipe&gt; &lt;pid&gt;", started through UAC on the technician's request.
/// The worker only reads the Wi-Fi profiles it is asked for and sends their XML back over an ACL'd pipe; it never
/// writes a file. The XML (key in clear) stays in memory until the backup writes it to Network\WiFi; it is never
/// displayed or logged. Everything else in DataBridge keeps the session rights.
/// </summary>
public static class WifiKeyElevation
{
    /// <summary>Command line switch of the elevated worker instance.</summary>
    public const string Argument = "--wifi-key-worker";

    private const int MaxProfiles = 512;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Shows the UAC prompt and asks the elevated worker for these profiles. Throws OperationCanceledException when
    /// the prompt is refused. <paramref name="elevate"/> = false starts the worker without UAC (Debug tests only).
    /// </summary>
    public static WifiKeyRecovery Recover(string exePath, IReadOnlyList<WifiProfileInfo> profiles, bool elevate = true)
    {
        Process? process = null;
        try
        {
            return Exchange(profiles, pipeName =>
            {
                try
                {
                    process = Process.Start(new ProcessStartInfo(exePath)
                    {
                        UseShellExecute = true,
                        Verb = elevate ? "runas" : "",
                        Arguments = $"{Argument} {pipeName} {Environment.ProcessId}",
                        WindowStyle = ProcessWindowStyle.Hidden,
                    });
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    throw new OperationCanceledException("Demande administrateur refusée.");
                }
                return process?.WaitForExitAsync() ?? throw new InvalidOperationException("Le processus administrateur n'a pas démarré.");
            }, Timeout);
        }
        finally
        {
            try { if (process is { HasExited: false } && !process.WaitForExit(5000)) process.Kill(); } catch { }
            process?.Dispose();
        }
    }

    /// <summary>Pipe side of <see cref="Recover"/>; <paramref name="startWorker"/> starts the worker and returns a task that ends with it.</summary>
    internal static WifiKeyRecovery Exchange(IReadOnlyList<WifiProfileInfo> profiles, Func<string, Task> startWorker, TimeSpan timeout)
    {
        var requested = profiles.Take(MaxProfiles).ToList();
        var pipeName = "DataBridge-WifiKeys-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        using (var me = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // The worker may run under another administrator account (UAC credentials): administrators may connect.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        var workerExit = startWorker(pipeName);
        var connect = pipe.WaitForConnectionAsync();
        if (Task.WaitAny([connect, workerExit], timeout) != 0)
            throw new InvalidOperationException("Le processus administrateur ne s'est pas connecté.");

        KeyWorkerResponse? response;
        try
        {
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            var request = new KeyWorkerRequest
            {
                Profiles = requested.Select(p => new KeyWorkerProfile { InterfaceId = p.InterfaceId, Name = p.Name }).ToList(),
            };
            writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
            var read = reader.ReadLineAsync();
            if (!read.Wait(timeout) || read.Result is not { } line)
                throw new InvalidOperationException("Le processus administrateur s'est arrêté sans répondre.");
            response = JsonSerializer.Deserialize<KeyWorkerResponse>(line, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or AggregateException)
        {
            throw new InvalidOperationException("Le processus administrateur s'est arrêté sans répondre.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Réponse inattendue du processus administrateur.");
        }
        if (response is null) throw new InvalidOperationException("Réponse inattendue du processus administrateur.");

        // Only the requested profiles are taken, and only when the returned XML really holds the key in clear.
        var recovery = new WifiKeyRecovery { Account = response.Account };
        foreach (var p in requested)
        {
            var r = response.Results.FirstOrDefault(x => x.Name == p.Name);
            if (r?.Xml is { } xml && WifiService.Analyze(xml).Key == WifiKeyStatus.Available)
                recovery.Recovered[p.Name] = xml;
            else
                recovery.StillProtected.Add(p.Name);
        }
        return recovery;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    /// <summary>Elevated side: reads the requested Wi-Fi profiles (key in clear when Windows allows it) and answers once.</summary>
    public static int RunWorker(string pipeName, int parentPid)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(30_000);
        // Only talk to the process that launched us (protects against a squatted pipe name).
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var serverPid) || serverPid != parentPid)
            return 2;

        using var reader = new StreamReader(pipe, new UTF8Encoding(false));
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        KeyWorkerRequest? request;
        try { request = reader.ReadLine() is { } line ? JsonSerializer.Deserialize<KeyWorkerRequest>(line, JsonOptions) : null; }
        catch { return 3; }
        if (request is null) return 3;

        var response = new KeyWorkerResponse();
        using (var me = WindowsIdentity.GetCurrent()) response.Account = me.Name;

        var opened = NativeMethods.WlanOpenHandle(NativeMethods.WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out var client);
        try
        {
            foreach (var p in request.Profiles.Take(MaxProfiles))
            {
                if (opened != 0)
                {
                    response.Results.Add(new KeyWorkerResult { Name = p.Name, Error = $"API WLAN indisponible (code {opened})." });
                    continue;
                }
                var xml = WifiService.GetProfileXml(client, p.InterfaceId, p.Name, out var error);
                response.Results.Add(xml is null
                    ? new KeyWorkerResult { Name = p.Name, Error = error == 5 ? "Accès refusé par Windows." : $"Erreur WLAN {error}." }
                    : new KeyWorkerResult { Name = p.Name, Xml = xml });
            }
        }
        finally
        {
            if (opened == 0) NativeMethods.WlanCloseHandle(client, IntPtr.Zero);
        }

        writer.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        return 0;
    }

    private sealed class KeyWorkerRequest
    {
        public List<KeyWorkerProfile> Profiles { get; set; } = [];
    }

    private sealed class KeyWorkerProfile
    {
        public Guid InterfaceId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class KeyWorkerResponse
    {
        public string Account { get; set; } = "";
        public List<KeyWorkerResult> Results { get; set; } = [];
    }

    private sealed class KeyWorkerResult
    {
        public string Name { get; set; } = "";
        public string? Xml { get; set; }
        public string? Error { get; set; }
    }
}

/// <summary>Outcome of <see cref="WifiKeyElevation.Recover"/>. <see cref="Recovered"/> holds keys in clear: never log it.</summary>
public sealed class WifiKeyRecovery
{
    /// <summary>Account the elevated worker ran under (the UAC credentials).</summary>
    public string Account { get; init; } = "";
    /// <summary>Profile name → profile XML with its key in clear.</summary>
    public Dictionary<string, string> Recovered { get; } = new(StringComparer.Ordinal);
    /// <summary>Profiles whose key is still not readable (per-user profile of another account, refused by Windows...).</summary>
    public List<string> StillProtected { get; } = [];
}
