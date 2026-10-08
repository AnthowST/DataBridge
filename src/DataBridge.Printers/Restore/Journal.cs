using System.Text.Json;
using DataBridge.Printers.Machine;
using DataBridge.Printers.Model;
using DataBridge.Printers.UserScope;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Restore;

public enum JournalAction { Created, Modified, Deleted, Unchanged, Failed }

public enum JournalObject { Printer, Port, DriverPackage, Driver, Connection, Settings, Security, DefaultPrinter }

public sealed class JournalEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    /// <summary>"Machine" (administrator) or "Utilisateur" (current profile).</summary>
    public string Scope { get; set; } = "";
    public JournalAction Action { get; set; }
    public JournalObject Object { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    /// <summary>JSON snapshot of the object before a modification or a deletion.</summary>
    public string? Before { get; set; }
}

/// <summary>Append-only record of every change, written to disk as it happens (survives a crash mid-restore).</summary>
public sealed class Journal
{
    private readonly object _lock = new();
    public string Path { get; }
    public List<JournalEntry> Entries { get; } = new();

    public Journal()
    {
        Path = System.IO.Path.Combine(OperationLog.LogsFolder, $"restauration-{DateTime.Now:yyyyMMdd-HHmmss}.journal.jsonl");
    }

    public void Add(string scope, JournalAction action, JournalObject obj, string name, string? detail = null, object? before = null)
    {
        var e = new JournalEntry { Scope = scope, Action = action, Object = obj, Name = name, Detail = detail, Before = before == null ? null : Json.Serialize(before) };
        lock (_lock)
        {
            Entries.Add(e);
            File.AppendAllText(Path, JsonSerializer.Serialize(e, Json.Compact) + Environment.NewLine);
        }
    }

    public IEnumerable<JournalEntry> Created => Entries.Where(e => e.Action == JournalAction.Created);

    /// <summary>
    /// Removes the printers, connections and ports created by this restoration (most recent first). Driver
    /// packages are left in the driver store: other printers may rely on them, and their removal is not reliably reversible.
    /// Modified or replaced objects are not rolled back automatically.
    /// </summary>
    public List<string> UndoCreated(IMachineOps? machine)
    {
        var results = new List<string>();
        foreach (var e in Created.Reverse().ToList())
        {
            OpResult r;
            switch (e.Object)
            {
                case JournalObject.Connection:
                    r = UserOps.DeleteConnection(e.Name);
                    break;
                case JournalObject.Printer when machine != null:
                    r = machine.DeletePrinter(e.Name);
                    break;
                case JournalObject.Port when machine != null:
                    r = machine.DeleteTcpPort(e.Name);
                    break;
                case JournalObject.Printer:
                case JournalObject.Port:
                    r = OpResult.Fail("droits administrateur non disponibles");
                    break;
                default:
                    continue;
            }
            Add(e.Scope, r.Ok ? JournalAction.Deleted : JournalAction.Failed, e.Object, e.Name, "Annulation : " + r.Message);
            results.Add((r.Ok ? "Annulé : " : "Non annulé : ") + r.Message);
        }
        return results;
    }
}
