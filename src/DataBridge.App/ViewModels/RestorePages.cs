using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.IO;
using DataBridge.Core.Restore;

namespace DataBridge.App.ViewModels;

/// <summary>A page of the restore wizard.</summary>
public abstract class RestorePageViewModel(RestoreViewModel restore, int number, string title, string glyph, string automationId)
    : WizardPage(number, title, glyph, automationId)
{
    protected RestoreViewModel Restore { get; } = restore;
    protected RestoreSelection? Sel => Restore.Selection;
    public string PageTitle => $"{Number}. {Title}";
}

// ====================================================================== 1. Sauvegarde

public sealed record BackupCandidate(string Name, string Path);

public sealed class RestoreSourceViewModel : RestorePageViewModel
{
    private string? _selectedPath;
    private bool _isChecking;
    private string? _error;
    private BackupContent? _content;
    private IReadOnlyList<BackupCandidate> _candidates = [];
    private string _sizeText = "";
    private string _filesText = "";

    public RestoreSourceViewModel(RestoreViewModel restore) : base(restore, 1, "Sauvegarde", "", "R_Nav_Source")
    {
        ChooseCommand = new RelayCommand(Choose, () => !IsChecking);
        UseCommand = new RelayCommand(() => { if (_content != null) Restore.LoadBackup(_content); }, () => _content != null && !IsChecking);
        UseCandidateCommand = new RelayCommand(p => { if (p is BackupCandidate c) Open(c.Path); });
        Status = "À choisir";
    }

    public ICommand ChooseCommand { get; }
    public ICommand UseCommand { get; }
    public ICommand UseCandidateCommand { get; }

    public string? SelectedPath { get => _selectedPath; private set => Set(ref _selectedPath, value); }
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }
    public string? Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error != null;
    public IReadOnlyList<BackupCandidate> Candidates { get => _candidates; private set { Set(ref _candidates, value); OnPropertyChanged(nameof(HasCandidates)); } }
    public bool HasCandidates => _candidates.Count > 0;

    public BackupContent? Content => _content;
    public bool HasContent => _content != null;
    public bool IsLegacy => _content?.IsLegacy == true;
    public string GenerationLabel => _content?.GenerationLabel ?? "";
    public string Computer => _content?.Source.Computer ?? "inconnu";
    public string User => _content?.Source.User ?? "inconnue";
    public string CreatedText => _content?.Source.CreatedAt is { } d ? d.ToString("dd.MM.yyyy HH:mm") : "inconnue";
    public string VersionText => _content?.VersionLabel ?? "";
    public string SourceProfile => _content?.Source.ProfilePath ?? "inconnu";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }
    public IReadOnlyList<string> Categories => _content?.CategoryLabels().ToList() ?? [];
    public IReadOnlyList<string> Warnings => _content?.Warnings ?? [];
    public bool HasWarnings => Warnings.Count > 0;
    public IReadOnlyList<string> SecurityIssues => _content?.SecurityIssues ?? [];
    public bool HasSecurityIssues => SecurityIssues.Count > 0;
    public bool IsInUse => _content != null && Restore.Backup == _content;

    private void Choose()
    {
        var dialog = new OpenFolderDialog { Title = "Choisir une sauvegarde DataBridge", Multiselect = false };
        if (_selectedPath != null && Directory.Exists(_selectedPath)) dialog.InitialDirectory = PathUtil.Parent(_selectedPath) ?? _selectedPath;
        if (dialog.ShowDialog() != true) return;
        Open(dialog.FolderName);
    }

    public void Open(string path)
    {
        SelectedPath = path;
        IsChecking = true;
        Error = null;
        Candidates = [];
        _content = null;
        Notify();
        Status = "Vérification…";
        Task.Run(() => BackupReader.Open(path)).ContinueWith(t =>
        {
            IsChecking = false;
            var result = t.IsCompletedSuccessfully ? t.Result : BackupOpenResult.Fail("Lecture impossible : " + t.Exception?.InnerException?.Message);
            if (SelectedPath != path) return;
            _content = result.Content;
            Error = result.Error;
            Candidates = result.Candidates.Select(c => new BackupCandidate(Path.GetFileName(c), c)).ToList();
            SizeText = _content?.TotalBytes is { } b ? Format.Size(b) : _content != null ? "Calcul…" : "";
            FilesText = _content?.TotalFiles is { } f ? Format.Count(f) : _content != null ? "Calcul…" : "";
            Status = _content != null ? Path.GetFileName(_content.Root) : "Sauvegarde non valide";
            IsDone = false;
            Notify();
            if (_content is { TotalFiles: null } c)
            {
                // 1.x backups have no totals: measure the backup folder itself (manifest-less).
                Task.Run(() =>
                {
                    using var scanner = new Core.Scanning.SizeScanner(1);
                    return scanner.GetOrCompute(c.Root);
                }).ContinueWith(s =>
                {
                    if (_content != c || !s.IsCompletedSuccessfully) return;
                    var data = s.Result;
                    SizeText = Format.Size(data.Bytes);
                    FilesText = Format.Count(data.Files) + " (dossier de sauvegarde, journal compris)";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            CommandManager.InvalidateRequerySuggested();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Notify()
    {
        foreach (var name in new[]
                 {
                     nameof(Content), nameof(HasContent), nameof(IsLegacy), nameof(GenerationLabel), nameof(Computer), nameof(User), nameof(CreatedText),
                     nameof(VersionText), nameof(SourceProfile), nameof(Categories), nameof(Warnings), nameof(HasWarnings), nameof(SecurityIssues),
                     nameof(HasSecurityIssues), nameof(IsInUse),
                 })
            OnPropertyChanged(name);
    }

    public override void OnActivated() => OnPropertyChanged(nameof(IsInUse));

    public void MarkUsed()
    {
        IsDone = true;
        OnPropertyChanged(nameof(IsInUse));
    }
}

// ====================================================================== 3. Fichiers supplémentaires

public sealed class RestoreAdditionalItemViewModel(RestoreAdditionalChoice choice, RestoreAdditionalViewModel owner) : ObservableObject
{
    private string _sizeText = choice.Item.Bytes is { } b ? Format.Size(b) : "";

    public RestoreAdditionalChoice Choice { get; } = choice;
    public string Name => Choice.Item.Name;
    public bool IsFile => Choice.Item.IsFile;
    public string Glyph => IsFile ? "" : "";
    public string SourceText => Choice.Item.RelativePath;
    public string OriginalText => Choice.Item.OriginalPath ?? "Chemin d'origine inconnu";
    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    public bool IsSelected
    {
        get => Choice.Selected;
        set
        {
            Choice.Selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedsDestination));
            owner.Changed();
        }
    }

    public string DestinationText => Choice.DestinationPath ?? "Destination à choisir";
    public bool HasDestination => Choice.HasDestination;
    public bool NeedsDestination => Choice.Selected && !Choice.HasDestination;
    public bool IsCustom => Choice.CustomParent != null;
    public string KindLabel => Choice.Kind switch
    {
        AdditionalDestinationKind.Original => "Destination originale",
        AdditionalDestinationKind.CurrentProfile => "Adaptée au profil actuel",
        AdditionalDestinationKind.Custom => "Destination choisie",
        _ => "Destination à choisir",
    };
    public string Note => Choice.CustomParent != null ? "Choisie par le technicien." : Choice.ProposalNote;

    public void Refresh()
    {
        foreach (var n in new[] { nameof(DestinationText), nameof(HasDestination), nameof(NeedsDestination), nameof(IsCustom), nameof(KindLabel), nameof(Note) })
            OnPropertyChanged(n);
    }
}

public sealed class RestoreAdditionalViewModel : RestorePageViewModel
{
    public RestoreAdditionalViewModel(RestoreViewModel restore) : base(restore, 3, "Fichiers supplémentaires", "", "R_Nav_Additional")
    {
        ChooseCommand = new RelayCommand(p => { if (p is RestoreAdditionalItemViewModel i) ChooseDestination(i); });
        ResetCommand = new RelayCommand(p => { if (p is RestoreAdditionalItemViewModel i) { i.Choice.CustomParent = null; i.Refresh(); Changed(); } });
        Status = "";
    }

    public ICommand ChooseCommand { get; }
    public ICommand ResetCommand { get; }
    public ObservableCollection<RestoreAdditionalItemViewModel> Items { get; } = [];
    public bool IsPresent => Items.Count > 0;

    public void Load()
    {
        Items.Clear();
        foreach (var c in Sel!.Additional) Items.Add(new RestoreAdditionalItemViewModel(c, this));
        OnPropertyChanged(nameof(IsPresent));
        UpdateSidebar();
    }

    private void ChooseDestination(RestoreAdditionalItemViewModel item)
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Choisir le dossier où restaurer « {item.Name} »",
            Multiselect = false,
        };
        if (item.Choice.DestinationPath is { } current && PathUtil.Parent(current) is { } parent && Directory.Exists(parent)) dialog.InitialDirectory = parent;
        if (dialog.ShowDialog() != true) return;
        var folder = PathUtil.Normalize(dialog.FolderName);
        if (RestorePaths.IsProtectedLocation(folder))
        {
            MainViewModel.ShowError("Ce dossier système ne peut pas recevoir une restauration. Choisissez un autre dossier.");
            return;
        }
        if (RestorePaths.IsInside(folder, Sel!.Backup.Root))
        {
            MainViewModel.ShowError("Ce dossier se trouve dans la sauvegarde elle-même. Choisissez un autre dossier.");
            return;
        }
        item.Choice.CustomParent = folder;
        item.Refresh();
        Changed();
    }

    public void Changed()
    {
        UpdateSidebar();
        Restore.SelectionChanged();
    }

    public void ApplyTotals(PlanTotals totals)
    {
        foreach (var item in Items.Where(i => !i.IsFile))
        {
            var job = totals.PerJob.Keys.FirstOrDefault(j => j.Category == RestoreCategories.Additional && PathUtil.AreSame(j.SourcePath, item.Choice.Item.FullPath));
            if (job != null && totals.PerJob[job].Complete) item.SizeText = $"{Format.Size(totals.PerJob[job].Bytes)} · {Format.Plural(totals.PerJob[job].Files, "fichier", "fichiers")}";
        }
    }

    private void UpdateSidebar()
    {
        if (Items.Count == 0) { Status = "Absent de la sauvegarde"; IsDone = false; return; }
        var selected = Items.Count(i => i.IsSelected);
        var missing = Items.Count(i => i.NeedsDestination);
        Status = missing > 0 ? $"{missing} destination(s) à choisir" : $"{selected}/{Items.Count} sélectionné(s)";
        IsDone = selected > 0 && missing == 0;
    }
}

// ====================================================================== 4. Signatures Outlook

public sealed class RestoreOutlookViewModel(RestoreViewModel restore) : RestorePageViewModel(restore, 4, "Signatures Outlook", "", "R_Nav_Outlook")
{
    public bool IsPresent => Sel?.Backup.Signatures != null;
    public IReadOnlyList<string> Names => Sel?.Backup.Signatures?.Names ?? [];
    public string CountText => IsPresent ? Format.Plural(Names.Count, "signature dans la sauvegarde", "signatures dans la sauvegarde") : "";
    public string SourceText => Sel?.Backup.Signatures?.RelativePath ?? "";
    public string OriginalText => Sel?.Backup.Signatures?.OriginalPath ?? "inconnu";
    public string DestinationText => Restore.Target.SignaturesPath;

    public bool Include
    {
        get => Sel?.IncludeSignatures == true && IsPresent;
        set
        {
            if (Sel is null) return;
            Sel.IncludeSignatures = value;
            OnPropertyChanged();
            UpdateSidebar();
            Restore.SelectionChanged();
        }
    }

    public void Load()
    {
        foreach (var n in new[] { nameof(IsPresent), nameof(Names), nameof(CountText), nameof(SourceText), nameof(OriginalText), nameof(DestinationText), nameof(Include) })
            OnPropertyChanged(n);
        UpdateSidebar();
    }

    private void UpdateSidebar()
    {
        Status = !IsPresent ? "Absent de la sauvegarde" : Include ? Format.Plural(Names.Count, "signature", "signatures") : "Non inclus";
        IsDone = Include;
    }
}

// ====================================================================== 5. Navigateurs

public sealed class RestoreTargetOptionViewModel(BrowserTargetOption option, RestoreBrowserProfileViewModel owner) : ObservableObject
{
    public BrowserTargetOption Option { get; } = option;
    public string Label => Option.Label;
    public string Detail => Option.Detail;
    public bool CreatesProfile => Option.CreatesProfile;
    public string GroupName => owner.GroupName;

    public bool IsChosen
    {
        get => owner.Choice.Target?.Key == Option.Key;
        set
        {
            if (!value) return;
            owner.Choose(Option);
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsChosen));
}

public sealed class RestoreBrowserProfileViewModel : ObservableObject
{
    private readonly RestoreBrowsersViewModel _owner;

    public RestoreBrowserProfileViewModel(RestoreBrowserChoice choice, RestoreBrowsersViewModel owner)
    {
        Choice = choice;
        _owner = owner;
        GroupName = "target-" + Guid.NewGuid().ToString("N");
        Options = choice.Options.Select(o => new RestoreTargetOptionViewModel(o, this)).ToList();
    }

    public RestoreBrowserChoice Choice { get; }
    public string GroupName { get; }
    public IReadOnlyList<RestoreTargetOptionViewModel> Options { get; }
    public string DisplayName => Choice.Profile.DisplayName;
    public string Folder => Choice.Profile.Folder;
    public string Account => Choice.Profile.Account ?? "";
    public bool IsActive => Choice.Profile.IsActive;
    public string AutomationName => $"{Choice.Browser.Name} {Choice.Profile.Folder}";

    public bool IsSelected
    {
        get => Choice.Selected;
        set
        {
            Choice.Selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NeedsChoice));
            _owner.Changed();
        }
    }

    public bool NeedsChoice => Choice.Selected && Choice.Target is null;
    public string TargetSummary => Choice.Target is { } t ? t.Label : "Profil de destination à choisir";

    public void Choose(BrowserTargetOption option)
    {
        Choice.Target = option;
        foreach (var o in Options) o.Refresh();
        OnPropertyChanged(nameof(NeedsChoice));
        OnPropertyChanged(nameof(TargetSummary));
        _owner.Changed();
    }
}

public sealed class RestoreBrowserCardViewModel : ObservableObject
{
    private bool _isRunning;

    public RestoreBrowserCardViewModel(BrowserKind kind, string name, IEnumerable<RestoreBrowserProfileViewModel> profiles)
    {
        Kind = kind;
        Name = name;
        Profiles = profiles.ToList();
        Description = BrowserRestoreService.Description(kind);
        RefreshRunning();
    }

    public BrowserKind Kind { get; }
    public string Name { get; }
    public IReadOnlyList<RestoreBrowserProfileViewModel> Profiles { get; }
    public IReadOnlyList<string> Description { get; }
    public string AutomationId => "RestoreBrowser_" + Kind;
    public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
    public string RunningText => $"{Name} est ouvert sur ce PC. Au lancement de la restauration, {AppInfo.Name} proposera de le fermer complètement (après confirmation).";
    public bool AnySelected => Profiles.Any(p => p.IsSelected);
    public string SelectionText => $"{Profiles.Count(p => p.IsSelected)} / {Profiles.Count} profil(s) sélectionné(s)";

    public void RefreshRunning() => IsRunning = BrowserProcessService.IsRunning(Kind);
    public void Notify()
    {
        OnPropertyChanged(nameof(AnySelected));
        OnPropertyChanged(nameof(SelectionText));
    }
}

public sealed class RestoreBrowsersViewModel : RestorePageViewModel
{
    private DateTime _lastRunningCheck = DateTime.MinValue;

    public RestoreBrowsersViewModel(RestoreViewModel restore) : base(restore, 5, "Navigateurs", "", "R_Nav_Browsers")
    {
        RefreshCommand = new RelayCommand(() => { foreach (var c in Cards) c.RefreshRunning(); });
        Status = "";
    }

    public ICommand RefreshCommand { get; }
    public ObservableCollection<RestoreBrowserCardViewModel> Cards { get; } = [];
    public bool IsPresent => Cards.Count > 0;

    public void Load()
    {
        Cards.Clear();
        foreach (var group in Sel!.Browsers.GroupBy(b => b.Browser.Kind))
            Cards.Add(new RestoreBrowserCardViewModel(group.Key, group.First().Browser.Name, group.Select(c => new RestoreBrowserProfileViewModel(c, this))));
        OnPropertyChanged(nameof(IsPresent));
        UpdateSidebar();
    }

    public void Changed()
    {
        foreach (var c in Cards) c.Notify();
        UpdateSidebar();
        Restore.SelectionChanged();
    }

    public override void OnTick()
    {
        if (DateTime.Now - _lastRunningCheck < TimeSpan.FromSeconds(3)) return;
        _lastRunningCheck = DateTime.Now;
        foreach (var c in Cards) c.RefreshRunning();
    }

    private void UpdateSidebar()
    {
        if (Cards.Count == 0) { Status = "Absent de la sauvegarde"; IsDone = false; return; }
        var chosen = Cards.Where(c => c.AnySelected).Select(c => BrowserProcessService.DisplayName(c.Kind).Split(' ')[^1]).ToList();
        var missing = Cards.SelectMany(c => c.Profiles).Count(p => p.NeedsChoice);
        Status = missing > 0 ? $"{missing} profil(s) à associer" : chosen.Count == 0 ? "Aucun" : string.Join(", ", chosen);
        IsDone = chosen.Count > 0 && missing == 0;
    }
}

// ====================================================================== 6. Réseaux Wi-Fi

public sealed class RestoreWifiItemViewModel(BackupWifiProfile profile, bool exists, RestoreWifiViewModel owner) : ObservableObject
{
    public BackupWifiProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Authentication => Profile.Authentication;
    public string KeyLabel => Profile.KeyLabel;
    public bool KeyIncluded => Profile.KeyIncluded;
    public bool ExistsOnThisPc { get; } = exists;

    public bool IsSelected
    {
        get => owner.IsSelected(Profile);
        set
        {
            owner.SetSelected(Profile, value);
            OnPropertyChanged();
        }
    }
}

public sealed class RestoreWifiViewModel : RestorePageViewModel
{
    public RestoreWifiViewModel(RestoreViewModel restore) : base(restore, 6, "Réseaux Wi-Fi", "", "R_Nav_Wifi")
    {
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        Status = "";
    }

    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ObservableCollection<RestoreWifiItemViewModel> Items { get; } = [];
    public bool IsPresent => Items.Count > 0;
    public bool ServiceAvailable => Sel?.WifiState?.Available != false;
    public string? ServiceMessage => Sel?.WifiState?.Message;

    public bool KeepExisting
    {
        get => Sel?.WifiExisting != WifiExistingPolicy.Replace;
        set
        {
            if (Sel is null || !value) return;
            Sel.WifiExisting = WifiExistingPolicy.Keep;
            Notify();
        }
    }

    public bool ReplaceExisting
    {
        get => Sel?.WifiExisting == WifiExistingPolicy.Replace;
        set
        {
            if (Sel is null || !value) return;
            Sel.WifiExisting = WifiExistingPolicy.Replace;
            Notify();
        }
    }

    public void Load()
    {
        Items.Clear();
        foreach (var w in Sel!.Backup.Wifi)
            Items.Add(new RestoreWifiItemViewModel(w, Sel.WifiState?.ExistingProfiles.Contains(w.Name) == true, this));
        OnPropertyChanged(nameof(IsPresent));
        OnPropertyChanged(nameof(ServiceAvailable));
        OnPropertyChanged(nameof(ServiceMessage));
        Notify();
    }

    public bool IsSelected(BackupWifiProfile p) => Sel?.SelectedWifi.Contains(p.RelativePath) == true;

    public void SetSelected(BackupWifiProfile p, bool value)
    {
        if (Sel is null) return;
        if (value) Sel.SelectedWifi.Add(p.RelativePath);
        else Sel.SelectedWifi.Remove(p.RelativePath);
        UpdateSidebar();
        Restore.SelectionChanged();
    }

    private void SetAll(bool value)
    {
        foreach (var item in Items) item.IsSelected = value;
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(KeepExisting));
        OnPropertyChanged(nameof(ReplaceExisting));
        UpdateSidebar();
        Restore.SelectionChanged();
    }

    private void UpdateSidebar()
    {
        if (Items.Count == 0) { Status = "Absent de la sauvegarde"; IsDone = false; return; }
        var n = Items.Count(i => i.IsSelected);
        Status = $"{n} / {Items.Count} profil(s)";
        IsDone = n > 0;
    }
}

// ====================================================================== 7. Fond d'écran

public sealed class RestoreWallpaperViewModel(RestoreViewModel restore) : RestorePageViewModel(restore, 7, "Fond d'écran", "", "R_Nav_Wallpaper")
{
    private ImageSource? _thumbnail;

    public bool IsPresent => Sel?.Backup.Wallpaper != null;
    public string SourceText => Sel?.Backup.Wallpaper?.RelativePath ?? "";
    public string SizeText => Sel?.Backup.Wallpaper is { } w ? Format.Size(w.Size) : "";
    public string DestinationText => Restore.Target.TranscodedWallpaperPath;
    public string ApplyText => Restore.Target.SkipWallpaperApply
        ? "Mode test : le fichier est restauré sans changer le fond d'écran affiché."
        : "Après la copie, DataBridge demande à Windows d'appliquer ce fond d'écran (réglage de la session, sans droits administrateur).";
    public ImageSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    public bool Include
    {
        get => Sel?.IncludeWallpaper == true && IsPresent;
        set
        {
            if (Sel is null) return;
            Sel.IncludeWallpaper = value;
            OnPropertyChanged();
            UpdateSidebar();
            Restore.SelectionChanged();
        }
    }

    public void Load()
    {
        Thumbnail = null;
        foreach (var n in new[] { nameof(IsPresent), nameof(SourceText), nameof(SizeText), nameof(DestinationText), nameof(ApplyText), nameof(Include) })
            OnPropertyChanged(n);
        UpdateSidebar();
    }

    public override void OnActivated()
    {
        if (_thumbnail != null || Sel?.Backup.Wallpaper is not { } wp) return;
        var path = wp.FullPath;
        Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }).ContinueWith(t => { if (t.IsCompletedSuccessfully) Thumbnail = t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void UpdateSidebar()
    {
        Status = !IsPresent ? "Absent de la sauvegarde" : Include ? "Inclus" : "Non inclus";
        IsDone = Include;
    }
}
