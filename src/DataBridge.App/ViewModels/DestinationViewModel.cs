using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.Win32;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;

namespace DataBridge.App.ViewModels;

public sealed class VolumeItem(VolumeInfo info)
{
    public VolumeInfo Info { get; } = info;
    public string Title => Info.Label;
    public string Root => Info.Root;
    public string Device => (Info.DeviceName is { Length: > 0 } d && d != Info.Label ? d : Info.KindLabel) + " · " + Info.FileSystem;
    public string DeviceName => Info.DeviceName is { Length: > 0 } d && d != Info.Label ? d : "";
    public string FileSystem => Info.FileSystem;
    public string FreeText => $"{Format.Size(Info.FreeBytes)} disponibles";
    public string TotalText => $"sur {Format.Size(Info.TotalBytes)}";
    public double UsedRatio => Info.TotalBytes > 0 ? 1 - (double)Info.FreeBytes / Info.TotalBytes : 0;
    public string UsedText => $"{UsedRatio * 100:0} % utilisés";
    /// <summary>Less than 10 % free: the usage bar turns amber.</summary>
    public bool IsAlmostFull => UsedRatio >= 0.9;
    public string KindLabel => Info.KindLabel;
    public bool IsExternal => Info.Kind is VolumeKind.Usb or VolumeKind.Removable;
    public bool IsNetwork => Info.Kind == VolumeKind.Network;
    public string AutomationId => "Volume_" + Info.Root.TrimEnd('\\').Replace(":", "");

    public string Glyph => Info.Kind switch
    {
        VolumeKind.Usb or VolumeKind.Removable => "",
        VolumeKind.Network => "",
        _ => "",
    };
}

public sealed class DestinationViewModel : PageViewModel
{
    private VolumeItem? _selectedVolume;
    private string? _selectedPath;
    private DestinationCheck? _check;
    private bool _checking;
    private string _folderPreview = "";
    private int? _profileDisk;
    private IReadOnlyList<string> _warnings = [];

    public DestinationViewModel(MainViewModel main) : base(main, 1, "Destination", "", "Nav_Destination")
    {
        ChooseFolderCommand = new RelayCommand(ChooseFolder);
        RefreshCommand = new RelayCommand(LoadVolumes);
        _profileDisk = VolumeService.GetDiskNumberForPath(main.Profile.ProfilePath);
        LoadVolumes();
        Status = "À choisir";
    }

    public ObservableCollection<VolumeItem> Volumes { get; } = [];
    public ICommand ChooseFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public VolumeItem? SelectedVolume
    {
        get => _selectedVolume;
        set
        {
            if (!Set(ref _selectedVolume, value) || value is null) return;
            SetPath(value.Root);
        }
    }

    public string? SelectedPath => _check?.IsValid == true ? _selectedPath : null;

    /// <summary>The backup folder would be inside a selected source (computed with the selection, see MainViewModel).</summary>
    public bool HasConflict => Main.Conflict != null && IsValid;
    public string ConflictDetail => Main.Conflict?.Detail ?? "";

    public void UpdateConflict()
    {
        OnPropertyChanged(nameof(HasConflict));
        OnPropertyChanged(nameof(ConflictDetail));
        UpdateSidebar();
    }
    public string? ChosenPath => _selectedPath;
    public DestinationCheck? Check => _check;
    public bool IsChecking { get => _checking; private set => Set(ref _checking, value); }
    public bool HasChoice => _selectedPath != null;
    public bool IsValid => _check?.IsValid == true;
    public string FolderPreview { get => _folderPreview; private set => Set(ref _folderPreview, value); }
    public IReadOnlyList<string> Warnings { get => _warnings; private set => Set(ref _warnings, value); }

    public string CheckText => _check switch
    {
        null when _selectedPath != null => "Vérification…",
        null => "",
        { IsValid: true } c => $"Accessible en écriture · {Format.Size(c.FreeBytes ?? 0)} disponibles" + (c.FileSystem != null ? $" · {c.FileSystem}" : ""),
        var c => c.Error ?? "Destination non valide.",
    };

    public long? FreeBytes => _check?.FreeBytes;

    private void LoadVolumes()
    {
        var current = _selectedVolume?.Root;
        Volumes.Clear();
        foreach (var v in VolumeService.ListVolumes().OrderBy(v => v.IsSystem).ThenBy(v => v.Kind == VolumeKind.Network).ThenBy(v => v.Root))
            Volumes.Add(new VolumeItem(v));
        if (current != null)
        {
            _selectedVolume = Volumes.FirstOrDefault(v => v.Root == current);
            OnPropertyChanged(nameof(SelectedVolume));
        }
        if (_selectedPath != null) SetPath(_selectedPath);
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir le dossier de destination de la sauvegarde",
            Multiselect = false,
        };
        if (_selectedPath != null) dialog.InitialDirectory = _selectedPath;
        if (dialog.ShowDialog() != true) return;

        var path = PathUtil.Normalize(dialog.FolderName);
        _selectedVolume = Volumes.FirstOrDefault(v => PathUtil.AreSame(v.Root, path));
        OnPropertyChanged(nameof(SelectedVolume));
        SetPath(path);
    }

    private void SetPath(string path)
    {
        _selectedPath = path;
        _check = null;
        OnPropertyChanged(nameof(HasChoice));
        OnPropertyChanged(nameof(ChosenPath));
        Notify();
        IsChecking = true;
        Task.Run(() => VolumeService.Check(path)).ContinueWith(t =>
        {
            if (_selectedPath != path) return;
            _check = t.Result;
            IsChecking = false;
            UpdateWarnings();
            Notify();
            Main.Additional.RefreshStatuses();
            Main.RequestRecalc();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(Check));
        OnPropertyChanged(nameof(CheckText));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(FreeBytes));
        UpdatePreview();
        UpdateSidebar();
    }

    private void UpdateWarnings()
    {
        var list = new List<string>();
        if (_selectedPath is null || _check is not { IsValid: true }) { Warnings = list; return; }

        var disk = VolumeService.GetDiskNumberForPath(_selectedPath);
        var sameVolume = string.Equals(Path.GetPathRoot(_selectedPath), Path.GetPathRoot(Main.Profile.ProfilePath), StringComparison.OrdinalIgnoreCase);
        if (sameVolume || (disk != null && disk == _profileDisk))
            list.Add("La destination est sur le même disque physique que le profil : ce n'est pas une sauvegarde indépendante du PC.");
        if (_check.IsFat32)
            list.Add("Système de fichiers FAT32 : les fichiers de plus de 4 Go ne pourront pas être copiés.");
        Warnings = list;
    }

    public void UpdatePreview()
    {
        var name = BackupNaming.FolderName(Main.Profile.MachineName, Main.Profile.UserName, DateTime.Now);
        FolderPreview = _selectedPath is null ? name : Path.Combine(_selectedPath, name);
    }

    public void UpdateSidebar()
    {
        IsDone = IsValid && !HasConflict;
        Status = _selectedPath is null ? "À choisir"
            : _check is null ? "Vérification…"
            : HasConflict ? "Destination invalide (dans une source)"
            : _check.IsValid ? $"{_selectedPath} · {Format.Size(_check.FreeBytes ?? 0)} libres"
            : "Destination non valide";
    }

    public override void OnActivated()
    {
        UpdatePreview();
        if (_selectedPath != null) SetPath(_selectedPath); // free space may have changed
    }

    public override void OnTick() => UpdatePreview();
}
