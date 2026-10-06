using System.Security.Cryptography;
using DataBridge.Core.Copy;
using Xunit.Abstractions;

namespace DataBridge.Tests;

/// <summary>V1.1: what the copy keeps from each file (no ACL / NTFS engine, just a check).</summary>
public class MetadataTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Copy_keeps_name_extension_content_and_last_write_time()
    {
        using var t = new TempTree();
        var (selection, _) = CountersTests.SelectionWithDocuments(t);
        var source = t.P(@"Documents\Factures\Facture été 2001 (n°42).PDF");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var content = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
        File.WriteAllBytes(source, content);
        var lastWrite = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc).AddTicks(1234567); // identifiable, sub-second
        var creation = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetCreationTimeUtc(source, creation);
        File.SetLastWriteTimeUtc(source, lastWrite);
        File.SetAttributes(source, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);

        var dest = t.Dir("Dest");
        var result = await new CopyEngine(selection.BuildPlan(dest, "Backup"), new CopyProgress()).RunAsync(CancellationToken.None);
        var copy = Path.Combine(dest, @"Backup\Personal\Documents\Factures\Facture été 2001 (n°42).PDF");

        Assert.Equal(CopyOutcome.Completed, result.Outcome);
        Assert.True(File.Exists(copy));
        Assert.Equal(Path.GetFileName(source), Path.GetFileName(copy));                 // name (accents, symbols)
        Assert.Equal(".PDF", Path.GetExtension(copy));                                  // extension, case kept
        Assert.Equal(SHA256.HashData(content), SHA256.HashData(File.ReadAllBytes(copy))); // content
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(copy));                        // modification date, exact

        // Reported as observed (not asserted): creation date and attributes.
        var copyAttributes = File.GetAttributes(copy);
        output.WriteLine($"CreationTime source={File.GetCreationTimeUtc(source):O} copie={File.GetCreationTimeUtc(copy):O}");
        output.WriteLine($"Attributs    source={File.GetAttributes(source)} copie={copyAttributes}");
        output.WriteLine($"Dossier      source={Directory.GetLastWriteTimeUtc(Path.GetDirectoryName(source)!):O} copie={Directory.GetLastWriteTimeUtc(Path.GetDirectoryName(copy)!):O}");
    }
}
