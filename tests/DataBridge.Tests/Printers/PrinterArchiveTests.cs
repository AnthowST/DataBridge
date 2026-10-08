using System.IO.Compression;
using System.Text;
using DataBridge.Printers.Archive;
using DataBridge.Printers.Model;
using DataBridge.Printers.Util;

namespace DataBridge.Tests.Printers;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DataBridgeTests", Guid.NewGuid().ToString("N"));

    public ArchiveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>Builds a minimal valid archive; <paramref name="tamper"/> may alter the entries before zipping.</summary>
    private string BuildArchive(Action<Dictionary<string, byte[]>>? tamper = null, Action<Manifest>? editManifest = null, string printerName = "Imprimante « Comptabilité » — 2ᵉ étage")
    {
        var inventory = new InventoryDocument
        {
            Printers = { new PrinterEntry { Id = "P001", Name = printerName, Kind = PrinterKind.TcpIp, Support = SupportLevel.Supported, PortId = "T001", DriverName = "Pilote test" } },
            Ports = { new PortEntry { Id = "T001", Name = "IP_10.0.0.5", Kind = PortKind.StandardTcpIp, Protocol = "RAW", HostAddress = "10.0.0.5", PortNumber = 9100 } },
        };
        var files = new Dictionary<string, byte[]>
        {
            ["inventory.json"] = Encoding.UTF8.GetBytes(Json.Serialize(inventory)),
            ["printers/P001/devmode-global.bin"] = new byte[300],
        };
        var manifest = new Manifest { ToolVersion = "test", ExportedAt = DateTimeOffset.Now };
        foreach (var (k, v) in files) manifest.Files[k] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(v));
        editManifest?.Invoke(manifest);
        files["manifest.json"] = Encoding.UTF8.GetBytes(Json.Serialize(manifest));
        tamper?.Invoke(files);

        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".prnbak");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(data);
        }
        return path;
    }

    [Fact]
    public void ValidArchive_IsUsable_AndPreservesAccentedName()
    {
        using var a = ArchiveReader.Open(BuildArchive());
        Assert.True(a.IsUsable, string.Join(" | ", a.Checks.Select(c => c.Detail)));
        Assert.Empty(a.BadFiles);
        Assert.Equal("Imprimante « Comptabilité » — 2ᵉ étage", a.Inventory!.Printers[0].Name);
    }

    [Fact]
    public void ModifiedFile_IsDetected_AndExcluded()
    {
        using var a = ArchiveReader.Open(BuildArchive(f => f["printers/P001/devmode-global.bin"] = new byte[301]));
        Assert.True(a.IsUsable); // the inventory is intact: other items remain restorable
        Assert.Contains("printers/P001/devmode-global.bin", a.BadFiles);
        Assert.Null(a.VerifiedPath("printers/P001/devmode-global.bin"));
        Assert.Contains(a.Checks, c => c.Level == CheckLevel.Error && c.Detail.Contains("Empreinte incorrecte"));
    }

    [Fact]
    public void MissingFile_IsDetected()
    {
        using var a = ArchiveReader.Open(BuildArchive(f => f.Remove("printers/P001/devmode-global.bin")));
        Assert.Contains("printers/P001/devmode-global.bin", a.BadFiles);
    }

    [Fact]
    public void CorruptInventory_MakesArchiveUnusable()
    {
        using var a = ArchiveReader.Open(BuildArchive(f => f["inventory.json"] = Encoding.UTF8.GetBytes("{ pas du json")));
        Assert.False(a.IsUsable);
    }

    [Fact]
    public void MissingManifest_MakesArchiveUnusable()
    {
        using var a = ArchiveReader.Open(BuildArchive(f => f.Remove("manifest.json")));
        Assert.False(a.IsUsable);
        Assert.Contains(a.Checks, c => c.Label == "Manifeste");
    }

    [Fact]
    public void UnknownMajorVersion_IsRefused()
    {
        using var a = ArchiveReader.Open(BuildArchive(editManifest: m => m.FormatVersion = "2.0"));
        Assert.False(a.IsUsable);
    }

    [Fact]
    public void TruncatedZip_IsRefused()
    {
        string path = BuildArchive();
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        using var a = ArchiveReader.Open(path);
        Assert.False(a.IsUsable);
    }

    [Fact]
    public void NotAZip_IsRefused()
    {
        string path = Path.Combine(_dir, "faux.prnbak");
        File.WriteAllText(path, "ceci n'est pas une archive");
        using var a = ArchiveReader.Open(path);
        Assert.False(a.IsUsable);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("printers/../../evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("printers/P001/file.txt:stream")]
    [InlineData("printers\\..\\..\\evil.txt")]
    [InlineData("printers//double")]
    public void PathTraversal_IsRejected(string entryName)
    {
        Assert.NotNull(ArchiveReader.ValidateEntryName(entryName));
        string path = BuildArchive(f => f[entryName] = new byte[] { 1 });
        using var a = ArchiveReader.Open(path);
        Assert.False(a.IsUsable);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
    }

    [Fact]
    public void UnlistedFile_IsIgnoredAndDeleted()
    {
        using var a = ArchiveReader.Open(BuildArchive(f => f["drivers/D001/package/setup.exe"] = new byte[] { 0x4D, 0x5A }));
        Assert.True(a.IsUsable);
        Assert.Contains(a.Checks, c => c.Level == CheckLevel.Warning && c.Detail.Contains("setup.exe"));
        Assert.False(File.Exists(Path.Combine(a.Root, "drivers", "D001", "package", "setup.exe")));
    }

    [Fact]
    public void Extraction_IsDeletedOnDispose()
    {
        string root;
        using (var a = ArchiveReader.Open(BuildArchive())) root = a.Root;
        Assert.False(Directory.Exists(root));
    }
}
