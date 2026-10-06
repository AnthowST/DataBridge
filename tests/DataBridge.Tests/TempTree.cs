namespace DataBridge.Tests;

/// <summary>Disposable temporary directory with helpers to create files of a given size.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "DataBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string P(string relative) => Path.Combine(Root, relative);

    public string File(string relative, int size)
    {
        var path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    public string Dir(string relative)
    {
        var path = P(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            // Read-only / offline attributes set by some tests would block the deletion.
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch
        {
            // best effort
        }
    }
}
