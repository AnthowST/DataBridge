using System.Runtime.InteropServices;
using DataBridge.Core.IO;
using DataBridge.Core.Native;

namespace DataBridge.Core.Profile;

public enum PersonalFolderKind { Desktop, Documents, Downloads, Pictures, Videos, Music }

/// <summary>A personal folder to back up: Known Folder id, French label and destination folder name.</summary>
public sealed record PersonalFolderDefinition(PersonalFolderKind Kind, Guid Id, string DisplayName, string DestinationName);

public static class KnownFolders
{
    public static readonly Guid Profile = new("5E6C858F-0E22-4760-9AFE-EA3317B67173");
    public static readonly Guid RoamingAppData = new("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D");
    public static readonly Guid LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    public static readonly Guid UserProfiles = new("0762D272-C50A-4BB0-A382-697DCD729B80");

    public static readonly IReadOnlyList<PersonalFolderDefinition> Personal =
    [
        new(PersonalFolderKind.Desktop, new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), "Bureau", "Desktop"),
        new(PersonalFolderKind.Documents, new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), "Documents", "Documents"),
        new(PersonalFolderKind.Downloads, new Guid("374DE290-123F-4565-9164-39C4925E467B"), "Téléchargements", "Downloads"),
        new(PersonalFolderKind.Pictures, new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB"), "Images", "Pictures"),
        new(PersonalFolderKind.Videos, new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC"), "Vidéos", "Videos"),
        new(PersonalFolderKind.Music, new Guid("4BD8D571-6D19-48D3-BE97-422220080E43"), "Musique", "Music"),
    ];

    /// <summary>Real (possibly redirected) path of a Known Folder for the current user, or null.</summary>
    public static string? GetPath(Guid id)
    {
        var hr = NativeMethods.SHGetKnownFolderPath(id, NativeMethods.KF_FLAG_DONT_VERIFY, IntPtr.Zero, out var ptr);
        try
        {
            if (hr != 0 || ptr == IntPtr.Zero) return null;
            var path = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrWhiteSpace(path) ? null : PathUtil.Normalize(path);
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }
}

public enum FolderLocation { Local, OneDrive, Network }

/// <summary>Detected state of one personal Known Folder.</summary>
public sealed record PersonalFolderInfo(
    PersonalFolderDefinition Definition,
    string? Path,
    bool Exists,
    FolderLocation Location,
    OneDriveRoot? OneDrive)
{
    public string LocationLabel => Location switch
    {
        FolderLocation.OneDrive => OneDrive?.Title ?? "OneDrive",
        FolderLocation.Network => "Réseau (redirigé)",
        _ => "Local",
    };

    /// <summary>OneDrive account associated with this folder (null = local folder, or OneDrive account not identified).</summary>
    public OneDriveAccount? OneDriveAccount => OneDrive?.Account;

    public static IReadOnlyList<PersonalFolderInfo> DetectAll(IReadOnlyList<OneDriveRoot> oneDriveRoots,
        IReadOnlyDictionary<PersonalFolderKind, string>? overrides = null)
    {
        var list = new List<PersonalFolderInfo>();
        foreach (var def in KnownFolders.Personal)
        {
            string? path = overrides != null && overrides.TryGetValue(def.Kind, out var o) ? PathUtil.Normalize(o) : KnownFolders.GetPath(def.Id);
            var exists = path != null && Directory.Exists(path);
            var od = path != null ? OneDriveDetector.Classify(path, oneDriveRoots) : null;
            var location = od != null ? FolderLocation.OneDrive
                : path != null && path.StartsWith(@"\\", StringComparison.Ordinal) ? FolderLocation.Network
                : FolderLocation.Local;
            list.Add(new PersonalFolderInfo(def, path, exists, location, od));
        }
        return list;
    }
}
