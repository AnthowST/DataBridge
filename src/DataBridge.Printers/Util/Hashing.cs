using System.Security.Cryptography;

namespace DataBridge.Printers.Util;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256Stream(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
}

/// <summary>Timestamped operation log, kept in memory and written to the archive / the logs folder.</summary>
public sealed class OperationLog
{
    private readonly List<string> _lines = new();
    private readonly object _lock = new();
    public event Action<string>? LineAdded;

    public void Info(string message) => Add("INFO ", message);
    public void Warn(string message) => Add("ATTENTION", message);
    public void Error(string message) => Add("ERREUR", message);

    private void Add(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (_lock) _lines.Add(line);
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Lines
    {
        get { lock (_lock) return _lines.ToList(); }
    }

    public string Text => string.Join(Environment.NewLine, Lines);

    public static string LogsFolder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DataBridge", "Logs", "Imprimantes");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
