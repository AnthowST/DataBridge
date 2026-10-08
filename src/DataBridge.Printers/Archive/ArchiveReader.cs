using System.IO.Compression;
using DataBridge.Printers.Model;
using DataBridge.Printers.Util;

namespace DataBridge.Printers.Archive;

public enum CheckLevel { Ok, Info, Warning, Error }

public sealed record ArchiveCheck(CheckLevel Level, string Label, string Detail);

/// <summary>An archive extracted to a private temporary folder, with its verification results.</summary>
public sealed class OpenedArchive : IDisposable
{
    public string SourcePath { get; init; } = "";
    public string Root { get; init; } = "";
    public Manifest? Manifest { get; set; }
    public InventoryDocument? Inventory { get; set; }
    public List<ArchiveCheck> Checks { get; } = new();
    /// <summary>Files listed in the manifest that are missing or whose hash does not match.</summary>
    public HashSet<string> BadFiles { get; } = new(StringComparer.Ordinal);
    public bool IsUsable => Manifest != null && Inventory != null && !Checks.Any(c => c.Level == CheckLevel.Error && c.Label != "Fichier");

    /// <summary>Absolute path of a verified file of the archive, or null when it is missing / corrupt / not in the manifest.</summary>
    public string? VerifiedPath(string? relative)
    {
        if (relative == null || Manifest == null) return null;
        if (!Manifest.Files.ContainsKey(relative) || BadFiles.Contains(relative)) return null;
        string full = ArchiveReader.SafeCombine(Root, relative) ?? throw new InvalidOperationException("Chemin hors de l'archive.");
        return File.Exists(full) ? full : null;
    }

    public byte[]? ReadVerified(string? relative)
    {
        var p = VerifiedPath(relative);
        return p == null ? null : File.ReadAllBytes(p);
    }

    /// <summary>True when every manifest file under <paramref name="folder"/> is present and intact.</summary>
    public bool FolderIntact(string folder)
    {
        if (Manifest == null) return false;
        string prefix = folder.TrimEnd('/') + "/";
        var files = Manifest.Files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return files.Count > 0 && files.All(f => !BadFiles.Contains(f));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { }
    }
}

public static class ArchiveReader
{
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024;
    public const int MaxEntries = 50_000;

    public static OpenedArchive Open(string archivePath)
    {
        string root = Path.Combine(Path.GetTempPath(), "DataBridge", "printers-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var opened = new OpenedArchive { SourcePath = archivePath, Root = root };
        try
        {
            Extract(archivePath, opened);
            if (opened.Checks.Any(c => c.Level == CheckLevel.Error)) return opened;
            Verify(opened);
        }
        catch (InvalidDataException ex)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive", $"Le fichier n'est pas une archive ZIP valide ou est endommagé : {ex.Message}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive", $"Lecture impossible : {ex.Message}"));
        }
        return opened;
    }

    /// <summary>Returns the absolute path for an archive entry, or null when it would escape <paramref name="root"/>.</summary>
    public static string? SafeCombine(string root, string entryName)
    {
        string? error = ValidateEntryName(entryName);
        if (error != null) return null;
        string rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        string full = Path.GetFullPath(Path.Combine(rootFull, entryName.Replace('/', '\\')));
        return full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Rejects absolute paths, drive letters, alternate data streams, "." / ".." segments and empty segments.</summary>
    public static string? ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "nom vide";
        string n = name.Replace('\\', '/');
        if (n.StartsWith('/')) return "chemin absolu";
        if (n.Contains(':')) return "lecteur ou flux alternatif";
        if (n.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || n.Any(ch => ch < 32)) return "caractère interdit";
        foreach (var seg in n.TrimEnd('/').Split('/'))
        {
            if (seg.Length == 0) return "segment vide";
            if (seg == "." || seg == "..") return "remontée de dossier";
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return "nom de fichier ambigu";
        }
        return null;
    }

    private static void Extract(string archivePath, OpenedArchive opened)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaxEntries)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive", $"Trop d'éléments ({zip.Entries.Count})."));
            return;
        }
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // directory
            string? reason = ValidateEntryName(entry.FullName);
            string? dest = reason == null ? SafeCombine(opened.Root, entry.FullName) : null;
            if (dest == null)
            {
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive",
                    $"Élément refusé « {entry.FullName} » ({reason ?? "sort du dossier d'extraction"}). L'archive n'est pas utilisée."));
                return;
            }
            total += entry.Length;
            if (total > MaxTotalBytes)
            {
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive", "Taille décompressée excessive : archive refusée."));
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Copy at most the declared length (+1 to detect a lying header): protects against decompression bombs.
            using var input = entry.Open();
            using var output = File.Create(dest);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > entry.Length)
                {
                    opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Archive", $"Taille incohérente pour « {entry.FullName} »."));
                    return;
                }
                output.Write(buffer, 0, read);
            }
        }
    }

    private static void Verify(OpenedArchive opened)
    {
        string manifestPath = Path.Combine(opened.Root, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Manifeste", "manifest.json absent : ce fichier n'est pas une sauvegarde d'imprimantes (.prnbak)."));
            return;
        }
        Manifest? manifest;
        try { manifest = Json.Deserialize<Manifest>(File.ReadAllText(manifestPath)); }
        catch (Exception ex)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Manifeste", $"Manifeste illisible : {ex.Message}"));
            return;
        }
        if (manifest == null || manifest.Format != Manifest.FormatName)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Manifeste", "Format inconnu : ce fichier n'est pas une sauvegarde d'imprimantes (.prnbak)."));
            return;
        }
        if (!Version.TryParse(manifest.FormatVersion, out var fv) || fv.Major != Manifest.CurrentMajor)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Manifeste", $"Version de format {manifest.FormatVersion} non prise en charge par cette version de DataBridge."));
            return;
        }
        opened.Manifest = manifest;
        opened.Checks.Add(new ArchiveCheck(CheckLevel.Ok, "Manifeste",
            $"Format {manifest.FormatVersion}, export du {manifest.ExportedAt:dd/MM/yyyy HH:mm} sur {manifest.Source.ComputerName} ({manifest.Source.OsProductName}, build {manifest.Source.OsBuild}, {manifest.Source.Architecture})."));

        int ok = 0;
        foreach (var (rel, hash) in manifest.Files)
        {
            string? full = SafeCombine(opened.Root, rel);
            if (full == null)
            {
                opened.BadFiles.Add(rel);
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Fichier", $"Chemin refusé dans le manifeste : « {rel} »."));
                continue;
            }
            if (!File.Exists(full))
            {
                opened.BadFiles.Add(rel);
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Fichier", $"Fichier manquant : {rel}"));
                continue;
            }
            if (!string.Equals(Hashing.Sha256File(full), hash, StringComparison.OrdinalIgnoreCase))
            {
                opened.BadFiles.Add(rel);
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Fichier", $"Empreinte incorrecte (fichier modifié ou endommagé) : {rel}"));
                continue;
            }
            ok++;
        }
        var listed = new HashSet<string>(manifest.Files.Keys, StringComparer.OrdinalIgnoreCase) { "manifest.json" };
        foreach (var file in Directory.EnumerateFiles(opened.Root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(opened.Root, file).Replace('\\', '/');
            if (!listed.Contains(rel))
            {
                opened.Checks.Add(new ArchiveCheck(CheckLevel.Warning, "Fichier", $"Fichier non référencé par le manifeste, ignoré : {rel}"));
                File.Delete(file);
            }
        }
        opened.Checks.Add(new ArchiveCheck(opened.BadFiles.Count == 0 ? CheckLevel.Ok : CheckLevel.Warning, "Intégrité",
            $"{ok} fichier(s) sur {manifest.Files.Count} vérifiés par empreinte SHA-256." +
            (opened.BadFiles.Count > 0 ? $" {opened.BadFiles.Count} fichier(s) manquant(s) ou endommagé(s) : les éléments concernés ne seront pas restaurés." : "")));

        if (opened.VerifiedPath(manifest.InventoryFile) is not string inventoryPath)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Inventaire", "Inventaire manquant ou endommagé : restauration impossible."));
            return;
        }
        try
        {
            opened.Inventory = Json.Deserialize<InventoryDocument>(File.ReadAllText(inventoryPath));
        }
        catch (Exception ex)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Inventaire", $"Inventaire illisible : {ex.Message}"));
            return;
        }
        if (opened.Inventory == null)
        {
            opened.Checks.Add(new ArchiveCheck(CheckLevel.Error, "Inventaire", "Inventaire vide."));
            return;
        }
        opened.Checks.Add(new ArchiveCheck(CheckLevel.Ok, "Inventaire",
            $"{opened.Inventory.Printers.Count} imprimante(s), {opened.Inventory.Ports.Count} port(s), {opened.Inventory.Drivers.Count} pilote(s)."));
        opened.Checks.Add(new ArchiveCheck(CheckLevel.Info, "Confiance",
            "Les empreintes prouvent que l'archive n'a pas été modifiée depuis l'export, pas que sa source est fiable. N'utilisez que des sauvegardes dont vous connaissez l'origine. Aucun script de l'archive n'est exécuté ; les pilotes sont installés par Windows, qui vérifie leur signature."));
    }
}
