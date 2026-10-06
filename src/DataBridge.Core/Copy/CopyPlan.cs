using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Profile;
using DataBridge.Core.Selection;

namespace DataBridge.Core.Copy;

public static class Categories
{
    public const string Personal = "Dossiers personnels";
    public const string Additional = "Fichiers supplémentaires";
    public const string Outlook = "Outlook";
    public const string Browsers = "Navigateurs";
    public const string Windows = "Windows";
    public const string Network = "Réseau";
}

/// <summary>
/// One unit of copy: a source (file or folder), where it goes, and exactly what is included.
/// The copy engine and the size estimate both work from these jobs, so the estimate matches the copy.
/// </summary>
public sealed class CopyJob
{
    public required string Category { get; init; }
    public required string Label { get; init; }
    public required string SourcePath { get; init; }
    /// <summary>Relative to the backup folder. For a file job this is the target file path.</summary>
    public required string DestinationRelative { get; init; }
    public bool IsFile { get; init; }
    /// <summary>What is included beneath <see cref="SourcePath"/> (null = everything).</summary>
    public SelectionSnapshot? Selection { get; init; }
    /// <summary>Paths handled by another job or excluded on purpose (anti-duplicates, backup folder...).</summary>
    public ICoverage? Coverage { get; init; }
    /// <summary>Same as <see cref="Coverage"/> without the backup folder exclusion: paths copied by another job.</summary>
    public ICoverage? HandledElsewhere { get; init; }
    /// <summary>Human readable notes for the log (e.g. excluded cache folders).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record WifiJob(IReadOnlyList<WifiProfileInfo> Profiles, string DestinationRelative);

public sealed class CopyPlan
{
    public required ProfileInfo Profile { get; init; }
    public required string DestinationBase { get; init; }
    public required string BackupFolderName { get; init; }
    public required IReadOnlyList<CopyJob> Jobs { get; init; }
    public WifiJob? Wifi { get; init; }
    public bool SkipCloudOnly { get; init; } = true;
    /// <summary>Lines describing what was deliberately not selected (for the log).</summary>
    public IReadOnlyList<string> NotSelected { get; init; } = [];
    /// <summary>OneDrive accounts and Known Folder mapping, for the log (names, types and paths only: never e-mails or tokens).</summary>
    public IReadOnlyList<string> OneDriveLines { get; init; } = [];
    /// <summary>Timestamped events about closing browsers before the copy (written verbatim to the log). Never secrets.</summary>
    public IReadOnlyList<string> BrowserEvents { get; set; } = [];
    /// <summary>Identifier written in databridge-manifest.json (2.0).</summary>
    public string BackupId { get; init; } = Guid.NewGuid().ToString();
    /// <summary>What the selection knew about each job, used to write databridge-manifest.json after the copy.</summary>
    public Restore.ManifestSeed? ManifestSeed { get; init; }

    public string BackupRoot => Path.Combine(DestinationBase, BackupFolderName);
}

public static class BackupNaming
{
    /// <summary>DataBridge_NomPC_NomSession_AAAA-MM-JJ_HH-mm, valid for Windows.</summary>
    public static string FolderName(string machine, string user, DateTime when) =>
        $"{AppInfo.BackupPrefix}_{PathUtil.SanitizeSegment(machine, "PC")}_{PathUtil.SanitizeSegment(user, "Session")}_{when:yyyy-MM-dd_HH-mm}";

    /// <summary>Folder prefixes used by every version of the tool (current first): older backups stay recognisable.</summary>
    public static readonly IReadOnlyList<string> KnownPrefixes = [AppInfo.BackupPrefix + "_", "itelium_ProfileCopy_", "ProfileCopy_"];

    /// <summary>Title line written in every copy-log.txt, whatever the product name of the version that wrote it.</summary>
    public const string LogSignature = "Journal de sauvegarde du profil";

    public static bool HasKnownPrefix(string folderName) =>
        KnownPrefixes.Any(p => folderName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a folder is a backup made by this tool. The decision relies on the content (copy-log.txt signed by the
    /// tool), not only on the folder name, so a renamed backup or one from an older version is still recognised.
    /// </summary>
    public static bool LooksLikeBackup(string folder)
    {
        try
        {
            var log = Path.Combine(folder, "copy-log.txt");
            if (File.Exists(log))
            {
                using var reader = new StreamReader(log);
                for (var i = 0; i < 4 && reader.ReadLine() is { } line; i++)
                    if (line.Contains(LogSignature, StringComparison.Ordinal)) return true;
            }
            return HasKnownPrefix(Path.GetFileName(folder.TrimEnd('\\')))
                   && (Directory.Exists(Path.Combine(folder, "Personal")) || Directory.Exists(Path.Combine(folder, "Additional")));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Same name, suffixed (_2, _3...) if a folder already exists at the destination.</summary>
    public static string UniqueFolderName(string destinationBase, string baseName)
    {
        var name = baseName;
        var n = 2;
        while (Directory.Exists(Path.Combine(destinationBase, name)) || File.Exists(Path.Combine(destinationBase, name)))
            name = $"{baseName}_{n++}";
        return name;
    }
}
