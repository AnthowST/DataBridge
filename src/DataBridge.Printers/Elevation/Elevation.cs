using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Elevation;

internal sealed class WorkerRequest
{
    public int Id { get; set; }
    public string Op { get; set; } = "";
    public JsonElement Args { get; set; }
}

internal sealed class WorkerResponse
{
    public int Id { get; set; }
    public OpResult Result { get; set; } = new();
}

/// <summary>
/// Client side (non-elevated UI): starts "DataBridge.exe --printer-worker &lt;pipe&gt; &lt;pid&gt;" through UAC and forwards
/// machine operations to it. The UI keeps every per-user operation; the worker only receives the closed list
/// of <see cref="IMachineOps"/> operations. If the UAC prompt is answered with another administrator's
/// credentials, the worker runs under that account — which is why it never touches a user profile.
/// </summary>
public sealed class ElevatedMachineOps : IMachineOps
{
    private readonly NamedPipeServerStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly Process _process;
    private readonly object _lock = new();
    private int _nextId;

    public string WorkerAccount { get; private set; } = "";

    private ElevatedMachineOps(NamedPipeServerStream pipe, Process process)
    {
        _pipe = pipe;
        _process = process;
        _reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false), false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

    /// <summary>Shows the UAC prompt and connects to the elevated worker. Throws OperationCanceledException if refused.</summary>
    public static ElevatedMachineOps Start(string exePath, TimeSpan timeout)
    {
        string pipeName = "DataBridge-Printers-" + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity();
        using (var me = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // The worker may run under another administrator account (UAC credentials): administrators may connect.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"{WorkerHost.Argument} {pipeName} {Environment.ProcessId}",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            pipe.Dispose();
            throw new OperationCanceledException("Élévation refusée : les opérations administrateur n'ont pas été effectuées.");
        }
        if (process == null)
        {
            pipe.Dispose();
            throw new InvalidOperationException("Le processus administrateur n'a pas démarré.");
        }

        var connect = pipe.WaitForConnectionAsync();
        var exited = process.WaitForExitAsync();
        int which = Task.WaitAny(new Task[] { connect, exited }, timeout);
        if (which != 0)
        {
            pipe.Dispose();
            try { if (!process.HasExited) process.Kill(); } catch { }
            throw new InvalidOperationException("Le processus administrateur ne s'est pas connecté.");
        }
        var ops = new ElevatedMachineOps(pipe, process);
        var hello = ops.Call("Hello", new { });
        if (!hello.Ok)
        {
            ops.Dispose();
            throw new InvalidOperationException("Dialogue impossible avec le processus administrateur : " + hello.Message);
        }
        ops.WorkerAccount = hello.Value ?? "";
        return ops;
    }

    private OpResult Call(string op, object args)
    {
        lock (_lock)
        {
            var request = new WorkerRequest { Id = ++_nextId, Op = op, Args = JsonSerializer.SerializeToElement(args, Json.Options) };
            _writer.WriteLine(JsonSerializer.Serialize(request, Json.Compact));
            string? line = _reader.ReadLine();
            if (line == null) return OpResult.Fail("Le processus administrateur s'est arrêté.");
            var response = JsonSerializer.Deserialize<WorkerResponse>(line, Json.Options);
            if (response == null || response.Id != request.Id) return OpResult.Fail("Réponse inattendue du processus administrateur.");
            return response.Result;
        }
    }

    public OpResult StartSpooler() => Call(nameof(StartSpooler), new { });
    public OpResult StageDriverPackage(DriverPackageRequest request) => Call(nameof(StageDriverPackage), request);
    public OpResult InstallDriver(string driverName, string environment, string? storeInfPath) => Call(nameof(InstallDriver), new { driverName, environment, storeInfPath });
    public OpResult AddTcpPort(PortEntry port, string portName) => Call(nameof(AddTcpPort), new { port, portName });
    public OpResult ConfigureTcpPort(PortEntry port, string portName) => Call(nameof(ConfigureTcpPort), new { port, portName });
    public OpResult DeleteTcpPort(string portName) => Call(nameof(DeleteTcpPort), new { portName });
    public OpResult AddPrinter(PrinterCreateRequest request) => Call(nameof(AddPrinter), request);
    public OpResult DeletePrinter(string name) => Call(nameof(DeletePrinter), new { name });
    public OpResult SetDriverData(string printerName, List<PrinterDataValue> values) => Call(nameof(SetDriverData), new { printerName, values });
    public OpResult SetGlobalDevMode(string printerName, byte[] devMode) => Call(nameof(SetGlobalDevMode), new { printerName, devMode });
    public OpResult SetSecurity(string printerName, string sddl) => Call(nameof(SetSecurity), new { printerName, sddl });

    public void Dispose()
    {
        try { _pipe.Dispose(); } catch { }
        try { if (!_process.WaitForExit(5000)) _process.Kill(); } catch { }
    }
}

/// <summary>Elevated side: connects to the UI's pipe and executes the closed list of machine operations.</summary>
public static class WorkerHost
{
    /// <summary>Command line switch of the elevated worker instance ("DataBridge.exe --printer-worker &lt;pipe&gt; &lt;pid&gt;").</summary>
    public const string Argument = "--printer-worker";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    public static int Run(string pipeName, int parentPid)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
        pipe.Connect(30_000);
        // Only talk to the process that launched us (protects against a squatted pipe name).
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid) || serverPid != parentPid)
            return 2;

        using var reader = new StreamReader(pipe, new System.Text.UTF8Encoding(false));
        using var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        using var ops = new MachineOps();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            WorkerRequest? request;
            try { request = JsonSerializer.Deserialize<WorkerRequest>(line, Json.Options); }
            catch { return 3; }
            if (request == null) return 3;
            OpResult result;
            try { result = Dispatch(ops, request); }
            catch (Exception ex) { result = OpResult.Fail(ex.Message); }
            writer.WriteLine(JsonSerializer.Serialize(new WorkerResponse { Id = request.Id, Result = result }, Json.Compact));
        }
        return 0;
    }

    private static T Arg<T>(JsonElement args) => args.Deserialize<T>(Json.Options) ?? throw new InvalidDataException("Arguments manquants.");
    private static string Str(JsonElement args, string name) => args.GetProperty(name).GetString() ?? "";

    private static OpResult Dispatch(IMachineOps ops, WorkerRequest r)
    {
        var a = r.Args;
        return r.Op switch
        {
            "Hello" => OpResult.Success("ok", WindowsIdentity.GetCurrent().Name),
            nameof(IMachineOps.StartSpooler) => ops.StartSpooler(),
            nameof(IMachineOps.StageDriverPackage) => ops.StageDriverPackage(Arg<DriverPackageRequest>(a)),
            nameof(IMachineOps.InstallDriver) => ops.InstallDriver(Str(a, "driverName"), Str(a, "environment"),
                a.TryGetProperty("storeInfPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null),
            nameof(IMachineOps.AddTcpPort) => ops.AddTcpPort(a.GetProperty("port").Deserialize<PortEntry>(Json.Options)!, Str(a, "portName")),
            nameof(IMachineOps.ConfigureTcpPort) => ops.ConfigureTcpPort(a.GetProperty("port").Deserialize<PortEntry>(Json.Options)!, Str(a, "portName")),
            nameof(IMachineOps.DeleteTcpPort) => ops.DeleteTcpPort(Str(a, "portName")),
            nameof(IMachineOps.AddPrinter) => ops.AddPrinter(Arg<PrinterCreateRequest>(a)),
            nameof(IMachineOps.DeletePrinter) => ops.DeletePrinter(Str(a, "name")),
            nameof(IMachineOps.SetDriverData) => ops.SetDriverData(Str(a, "printerName"), a.GetProperty("values").Deserialize<List<PrinterDataValue>>(Json.Options)!),
            nameof(IMachineOps.SetGlobalDevMode) => ops.SetGlobalDevMode(Str(a, "printerName"), a.GetProperty("devMode").GetBytesFromBase64()),
            nameof(IMachineOps.SetSecurity) => ops.SetSecurity(Str(a, "printerName"), Str(a, "sddl")),
            _ => OpResult.Fail($"Opération inconnue refusée : {r.Op}"),
        };
    }
}
