using System.Runtime.InteropServices;
using System.Windows.Media;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;
using DataBridge.Core.Profile;
using DataBridge.Core.Restore;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

/// <summary>
/// One personal Known Folder as shown in the explorer (root row). For a restore, the tree is the folder of the backup
/// and <see cref="Restore"/> gives the Known Folder of this PC it goes to: location badges then describe the destination.
/// </summary>
public sealed class PersonalRootViewModel(PersonalRoot model, RestorePersonalChoice? restore = null) : ObservableObject
{
    private DirStats? _stats;
    private Totals? _selected;
    private CheckState _state = model.State;

    public PersonalRoot Model { get; } = model;
    public RestorePersonalChoice? Restore { get; } = restore;
    public bool IsRestore => Restore != null;
    /// <summary>The real Known Folder described by the badges: the source (backup) or the destination (restore).</summary>
    private PersonalFolderInfo? Place => Restore is null ? Model.Info : Restore.Target;
    public string Label => Model.Label;
    public string Path => Model.Info.Path ?? "Chemin inconnu";
    /// <summary>Backup: the folder of this PC. Restore: "Personal\Documents  →  C:\Users\X\Documents".</summary>
    public string PathLine => Restore is null ? Path
        : $"{Restore.Backup.RelativePath}  →  {Restore.DestinationPath ?? "dossier introuvable sur ce PC"}";
    public string PathTooltip => Restore is null ? Path
        : $"Source (sauvegarde) : {Restore.Backup.FullPath}" + Environment.NewLine + $"Destination : {Restore.DestinationPath ?? "introuvable"}"
          + (Restore.Backup.OriginalPath is { } o ? Environment.NewLine + $"Chemin d'origine : {o}" : "");
    public bool Available => Model.Available;
    public string LocationLabel => Place?.LocationLabel ?? "Introuvable sur ce PC";
    public bool IsOneDrive => Place?.Location == FolderLocation.OneDrive;
    public bool IsNetwork => Place?.Location == FolderLocation.Network;
    public string OneDriveName => Place?.OneDrive?.Label ?? "";
    /// <summary>"OneDrive · itelium", "OneDrive · Personnel" or "OneDrive".</summary>
    public string OneDriveTitle => Place?.OneDrive?.Title ?? "OneDrive";
    /// <summary>E-mail of the account, or "Compte non identifié" (empty when the title already identifies it).</summary>
    public string OneDriveDetail => Place?.OneDrive?.Detail ?? OneDriveAccount.Unidentified;
    public bool OneDriveUnidentified => Place?.OneDriveAccount is null;
    public string OneDriveTooltip => Place?.OneDrive is { } r
        ? $"Racine OneDrive : {r.Path}" + Environment.NewLine
          + (r.Account is { } a ? $"Compte : {a.TypeLabel} ({a.AccountId})" : "Compte non identifié de façon fiable")
          + (IsRestore ? Environment.NewLine + "Les fichiers restaurés dans ce dossier seront ensuite synchronisés par OneDrive." : "")
        : "";

    public string Glyph => Model.Info.Definition.Kind switch
    {
        PersonalFolderKind.Desktop => "",
        PersonalFolderKind.Documents => "",
        PersonalFolderKind.Downloads => "",
        PersonalFolderKind.Pictures => "",
        PersonalFolderKind.Videos => "",
        _ => "",
    };

    public CheckState State { get => _state; set { if (Set(ref _state, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(DetailText)); } } }

    public DirStats? Stats
    {
        get => _stats;
        set
        {
            if (!Set(ref _stats, value)) return;
            OnPropertyChanged(nameof(TotalText));
            OnPropertyChanged(nameof(CloudText));
            OnPropertyChanged(nameof(CloudTooltip));
            OnPropertyChanged(nameof(IsScanning));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(DetailText));
        }
    }

    public Totals? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(DetailText)); } }
    }

    public bool IsScanning => Available && _stats is null;

    /// <summary>What is physically on this PC. Cloud-only bytes are shown apart (<see cref="CloudText"/>).</summary>
    public string TotalText => !Available ? "Introuvable"
        : _stats is null ? "Analyse…"
        : _stats.CloudFiles > 0 ? $"{Format.Size(_stats.LocalBytes)} locaux"
        : Format.Size(_stats.Bytes);

    public string CloudText => _stats is { CloudFiles: > 0 } s ? $"{Format.Size(s.CloudBytes)} en ligne uniquement" : "";

    public string CloudTooltip => _stats is { CloudFiles: > 0 } s
        ? $"{Format.Plural(s.CloudFiles, "fichier disponible", "fichiers disponibles")} uniquement en ligne (OneDrive / iCloud) : " +
          $"{Format.Size(s.CloudBytes)} qui ne sont pas sur le disque de ce PC."
        : "";

    public string StatusText => !Available ? (IsRestore ? "Absent de cette sauvegarde" : "Dossier introuvable")
        : IsRestore && Restore!.DestinationPath is null && State != CheckState.Unchecked ? "Destination introuvable sur ce PC"
        : State == CheckState.Unchecked ? "Non inclus"
        : State == CheckState.Checked ? "Dossier complet"
        : _selected is { } sel ? $"Sélection partielle : {Format.Size(sel.LocalBytes)}" + (_stats != null ? $" sur {Format.Size(_stats.LocalBytes)}" : "")
        : "Sélection partielle";

    public string DetailText => _stats is { } s && Available ? $"{Format.Plural(s.Files, "élément", "éléments")} · {StatusText}" : StatusText;
}

/// <summary>Row of the flat, virtualized explorer list (root, folder or file).</summary>
public sealed class TreeRowViewModel : ObservableObject
{
    private readonly PersonalViewModel _owner;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _hasChildren;
    private CheckState _state;
    private long? _size;
    private int _cloudFiles;
    private long _cloudBytes;
    private string? _error;

    public TreeRowViewModel(PersonalViewModel owner, PersonalRootViewModel root, FsEntry? entry, int depth, string? boundaryLabel = null)
    {
        _owner = owner;
        Root = root;
        Entry = entry;
        Depth = depth;
        BoundaryLabel = boundaryLabel;
        FullPath = entry?.FullPath ?? root.Model.Path;
        IsDirectory = entry?.IsDirectory ?? true;
        _hasChildren = IsDirectory && root.Available && boundaryLabel is null;
        if (entry is { IsDirectory: false }) _size = entry.Size;
    }

    public PersonalRootViewModel Root { get; }
    public FsEntry? Entry { get; }
    public bool IsRoot => Entry is null;
    public int Depth { get; }
    public string FullPath { get; }
    public string Name => Entry?.Name ?? Root.Label;
    public bool IsDirectory { get; }
    public List<TreeRowViewModel>? Children { get; set; }

    /// <summary>Nested personal folder (handled in its own section): shown, but not selectable here.</summary>
    public string? BoundaryLabel { get; }
    public bool IsBoundary => BoundaryLabel != null;
    public bool CanToggle => !IsBoundary && Root.Available;

    public ImageSource? Icon => Entry is null ? null : IsDirectory ? ShellIcons.Folder : ShellIcons.ForFile(Entry.Extension);
    public bool IsHidden => Entry is { IsHidden: true };
    public bool IsCloudOnly => Entry is { IsCloudOnly: true };
    public bool IsLarge => !IsDirectory && (_size ?? 0) >= Format.OneGiB;
    public string Modified => Entry is { LastWriteUtc.Year: > 1601 } e ? e.LastWriteUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value || !_hasChildren) { OnPropertyChanged(); return; }
            _isExpanded = value;
            OnPropertyChanged();
            if (value) _owner.Expand(this);
            else _owner.Collapse(this);
        }
    }

    internal void SetExpandedSilently(bool value)
    {
        _isExpanded = value;
        OnPropertyChanged(nameof(IsExpanded));
    }

    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool HasChildren { get => _hasChildren; set => Set(ref _hasChildren, value); }
    public string? Error { get => _error; set => Set(ref _error, value); }

    public CheckState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(IsExcluded));
        }
    }

    /// <summary>Bound two-way to the checkbox. The value is ignored: clicking applies the toggle rule (partial → checked).</summary>
    public bool? IsChecked
    {
        get => _state switch { CheckState.Checked => true, CheckState.Unchecked => false, _ => null };
        set
        {
            _owner.Toggle(this);
            OnPropertyChanged();
        }
    }

    public bool IsExcluded => _state == CheckState.Unchecked;

    public long? Size
    {
        get => _size;
        set
        {
            if (!Set(ref _size, value)) return;
            OnPropertyChanged(nameof(SizeText));
            OnPropertyChanged(nameof(IsLarge));
        }
    }

    /// <summary>Folder sizes are what is on the disk; cloud-only content is shown apart.</summary>
    public void SetStats(DirStats stats)
    {
        Size = stats.LocalBytes;
        _cloudBytes = stats.CloudBytes;
        CloudFiles = (int)stats.CloudFiles;
        OnPropertyChanged(nameof(CloudFilesText));
        OnPropertyChanged(nameof(CloudFilesTooltip));
    }

    public int CloudFiles
    {
        get => _cloudFiles;
        private set { if (Set(ref _cloudFiles, value)) OnPropertyChanged(nameof(CloudFilesText)); }
    }

    public string CloudFilesText => _cloudFiles > 0 ? $"{Format.Size(_cloudBytes)} en ligne" : "";
    public string CloudFilesTooltip => _cloudFiles > 0
        ? $"{Format.Plural(_cloudFiles, "fichier disponible", "fichiers disponibles")} uniquement en ligne ({Format.Size(_cloudBytes)}), non présents sur le disque. La taille affichée est celle des fichiers locaux."
        : "";

    public string SizeText => _size is { } s ? Format.Size(s) : IsDirectory && !IsBoundary ? "…" : "";
}

/// <summary>Result row of the search panel. Toggling uses the same selection model as the tree.</summary>
public sealed class SearchResultViewModel(PersonalViewModel owner, PersonalRootViewModel root, FsEntry entry) : ObservableObject
{
    private CheckState _state;

    public PersonalRootViewModel Root { get; } = root;
    public FsEntry Entry { get; } = entry;
    public string FullPath => Entry.FullPath;
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public ImageSource? Icon => IsDirectory ? ShellIcons.Folder : ShellIcons.ForFile(Entry.Extension);
    public string Location
    {
        get
        {
            var parent = PathUtil.Parent(Entry.FullPath) ?? "";
            var rel = PathUtil.Relative(parent, Root.Model.Path);
            return rel.Length == 0 ? Root.Label : Root.Label + " › " + rel.Replace("\\", " › ");
        }
    }
    public long Size => Entry.Size;
    public string SizeText => IsDirectory ? "" : Format.Size(Entry.Size);
    public bool IsLarge => !IsDirectory && Entry.Size >= Format.OneGiB;
    public bool IsCloudOnly => Entry.IsCloudOnly;

    public CheckState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsChecked));
            OnPropertyChanged(nameof(IsExcluded));
        }
    }

    public bool? IsChecked
    {
        get => _state switch { CheckState.Checked => true, CheckState.Unchecked => false, _ => null };
        set
        {
            owner.ToggleResult(this);
            OnPropertyChanged();
        }
    }

    public bool IsExcluded => _state == CheckState.Unchecked;
}

internal static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    /// <summary>Explorer-like ordering ("file2" before "file10").</summary>
    public static readonly Comparison<string> Compare = (a, b) => StrCmpLogicalW(a, b);
}
