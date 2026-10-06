using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.IO;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

public sealed class AdditionalItemViewModel(AdditionalItem model, AdditionalViewModel owner) : ObservableObject
{
    private string _sizeText = "…";
    private AdditionalStatus? _status;
    private string _cloudText = "";

    public AdditionalItem Model { get; } = model;
    public string Name => Model.Name;
    public string Path => Model.Path;
    public bool IsDirectory => Model.IsDirectory;
    public string TypeLabel => IsDirectory ? "Dossier" : "Fichier";
    public ImageSource? Icon => IsDirectory ? ShellIcons.Folder : ShellIcons.ForFile(System.IO.Path.GetExtension(Model.Path));
    public ICommand RemoveCommand { get; } = new RelayCommand(() => owner.Remove(model));

    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public string CloudText { get => _cloudText; set => Set(ref _cloudText, value); }

    public AdditionalStatus? Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsDuplicate));
            OnPropertyChanged(nameof(IsPartial));
            OnPropertyChanged(nameof(IsMissing));
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(IsConflict));
        }
    }

    public string StatusText => _status?.Message ?? "";
    public bool IsDuplicate => _status?.Kind == AdditionalStatusKind.AlreadyIncluded;
    public bool IsPartial => _status?.Kind is AdditionalStatusKind.PartiallyIncluded or AdditionalStatusKind.ContainsPersonal;
    public bool IsMissing => _status?.Kind == AdditionalStatusKind.Missing;
    public bool IsReady => _status?.Kind == AdditionalStatusKind.Ready;
    /// <summary>Contains the backup folder (or is inside it): the launch is blocked until it is removed.</summary>
    public bool IsConflict => _status?.Kind == AdditionalStatusKind.ContainsDestination;
}

public sealed class AdditionalViewModel : PageViewModel
{
    private string _message = "";
    private bool _messageIsWarning;

    public AdditionalViewModel(MainViewModel main) : base(main, 3, "Fichiers et dossiers supplémentaires", "", "Nav_Additional")
    {
        AddFolderCommand = new RelayCommand(AddFolders);
        AddFilesCommand = new RelayCommand(AddFiles);
        Status = "Aucun ajout";
    }

    public ObservableCollection<AdditionalItemViewModel> Items { get; } = [];
    public ICommand AddFolderCommand { get; }
    public ICommand AddFilesCommand { get; }
    public string Message { get => _message; private set => Set(ref _message, value); }
    public bool MessageIsWarning { get => _messageIsWarning; private set => Set(ref _messageIsWarning, value); }
    public bool IsEmpty => Items.Count == 0;

    private void AddFolders()
    {
        var dialog = new OpenFolderDialog { Title = "Ajouter un ou plusieurs dossiers à la sauvegarde", Multiselect = true };
        if (dialog.ShowDialog() == true) Add(dialog.FolderNames, isDirectory: true);
    }

    private void AddFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Ajouter des fichiers à la sauvegarde",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "Tous les fichiers (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true) Add(dialog.FileNames, isDirectory: false);
    }

    /// <summary>Adds paths after validation (exists, not another user's profile, not already listed).</summary>
    public void Add(IEnumerable<string> paths, bool isDirectory)
    {
        var refused = new List<string>();
        var added = 0;
        foreach (var raw in paths)
        {
            string path;
            try { path = PathUtil.Normalize(raw); }
            catch { refused.Add($"{raw} : chemin invalide"); continue; }

            if (Main.Profile.IsInOtherUserProfile(path))
            {
                refused.Add($"{path} : appartient au profil d'un autre utilisateur (non pris en charge)");
                continue;
            }
            if (Main.Selection.Additional.Any(a => PathUtil.AreSame(a.Path, path)))
            {
                refused.Add($"{path} : déjà dans la liste");
                continue;
            }
            if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
            {
                refused.Add($"{path} : introuvable ou inaccessible");
                continue;
            }
            // The backup folder itself (or a folder containing it) can never be a source: it would copy itself.
            if (Main.BackupRootPreview is { } backupRoot && DestinationGuard.Overlap(path, backupRoot) is { } overlap)
            {
                refused.Add(overlap == OverlapKind.BackupInsideSource
                    ? $"{path} : contient la destination de la sauvegarde ({Main.Destination.ChosenPath}), il serait recopié dans lui-même"
                    : $"{path} : se trouve dans le dossier de sauvegarde");
                continue;
            }

            var item = new AdditionalItem(path, isDirectory);
            Main.Selection.Additional.Add(item);
            Items.Add(new AdditionalItemViewModel(item, this));
            if (isDirectory) Main.Scanner.Enqueue(path, priority: true);
            added++;
        }

        MessageIsWarning = refused.Count > 0;
        Message = refused.Count > 0
            ? "Non ajouté(s) : " + string.Join(" · ", refused)
            : added > 0 ? $"{Format.Plural(added, "élément ajouté", "éléments ajoutés")}." : "";
        OnPropertyChanged(nameof(IsEmpty));
        RefreshStatuses();
        Main.RequestRecalc();
    }

    public void Remove(AdditionalItem item)
    {
        Main.Selection.Additional.Remove(item);
        var vm = Items.FirstOrDefault(i => i.Model == item);
        if (vm != null) Items.Remove(vm);
        Message = "";
        OnPropertyChanged(nameof(IsEmpty));
        RefreshStatuses();
        Main.RequestRecalc();
    }

    /// <summary>Re-evaluates duplicates (depends on the personal folders selection too).</summary>
    public void RefreshStatuses()
    {
        var statuses = Main.Selection.AnalyzeAdditional(Main.BackupRootPreview);
        foreach (var vm in Items)
            vm.Status = statuses.TryGetValue(vm.Model, out var s) ? s : null;
        UpdateSidebar();
    }

    public override void OnTick() => UpdateSizes();

    private void UpdateSizes()
    {
        foreach (var vm in Items)
        {
            if (vm.IsDirectory)
            {
                if (Main.Scanner.TryGet(vm.Path, out var stats))
                {
                    vm.SizeText = stats.CloudFiles > 0
                        ? $"{Format.Size(stats.LocalBytes)} locaux · {Format.Plural(stats.Files, "fichier", "fichiers")}"
                        : $"{Format.Size(stats.Bytes)} · {Format.Plural(stats.Files, "fichier", "fichiers")}";
                    vm.CloudText = stats.CloudFiles > 0
                        ? $"{Format.Plural(stats.CloudFiles, "fichier", "fichiers")} disponibles uniquement en ligne ({Format.Size(stats.CloudBytes)})"
                        : "";
                }
            }
            else if (vm.SizeText == "…")
            {
                var entry = FastDirectory.GetEntry(vm.Path);
                vm.SizeText = entry is null ? "Introuvable" : Format.Size(entry.Size);
                vm.CloudText = entry is { IsCloudOnly: true } ? "Disponible uniquement en ligne" : "";
            }
        }
    }

    public void ApplyTotals(PlanTotals totals) => UpdateSidebar();

    private void UpdateSidebar()
    {
        var count = Items.Count;
        var dup = Items.Count(i => i.IsDuplicate);
        var conflicts = Items.Count(i => i.IsConflict);
        Status = count == 0 ? "Aucun ajout"
            : conflicts > 0 ? $"{Format.Plural(conflicts, "élément contient", "éléments contiennent")} la destination"
            : $"{Format.Plural(count, "élément", "éléments")}" + (dup > 0 ? $" ({dup} déjà inclus)" : "");
        IsDone = count > 0 && conflicts == 0;
    }

    public override void OnActivated()
    {
        RefreshStatuses();
        UpdateSizes();
    }
}
