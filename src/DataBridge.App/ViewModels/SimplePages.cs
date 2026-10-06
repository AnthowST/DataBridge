using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

// ====================================================================== Outlook

public sealed class OutlookViewModel : PageViewModel
{
    private string _sizeText = "";

    public OutlookViewModel(MainViewModel main) : base(main, 4, "Signatures Outlook", "", "Nav_Outlook")
    {
        UpdateSidebar();
    }

    public SignatureInfo? Info => Main.Selection.Signatures;
    public bool Exists => Info?.Exists == true;
    public IReadOnlyList<string> Names => Info?.Names ?? [];
    public string Path => Info?.Path ?? "";
    public string CountText => Exists ? $"{Format.Plural(Names.Count, "signature détectée", "signatures détectées")}" : "Aucun dossier de signatures pour cette session";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    public bool Include
    {
        get => Main.Selection.IncludeSignatures && Exists;
        set
        {
            Main.Selection.IncludeSignatures = value;
            OnPropertyChanged();
            UpdateSidebar();
            Main.RequestRecalc();
        }
    }

    public void ApplyTotals(PlanTotals totals)
    {
        var job = totals.PerJob.Keys.FirstOrDefault(j => j.Category == Categories.Outlook);
        if (job != null) SizeText = $"{Format.Size(totals.PerJob[job].Bytes)} · {Format.Plural(totals.PerJob[job].Files, "fichier", "fichiers")}";
    }

    private void UpdateSidebar()
    {
        Status = !Exists ? "Aucune signature" : Include ? $"{Format.Plural(Names.Count, "signature", "signatures")}" : "Non inclus";
        IsDone = Include;
    }
}

// ====================================================================== Wallpaper

public sealed class WallpaperViewModel : PageViewModel
{
    private ImageSource? _thumbnail;

    public WallpaperViewModel(MainViewModel main) : base(main, 7, "Fond d'écran", "", "Nav_Wallpaper")
    {
        UpdateSidebar();
    }

    public WallpaperInfo? Info => Main.Selection.Wallpaper;
    public bool Exists => Info?.Exists == true;
    public string Path => Info?.Path ?? "";
    public string DetailText => Exists ? $"TranscodedWallpaper détecté · {Format.Size(Info!.Size)} · modifié le {Info.LastWrite:dd.MM.yyyy HH:mm}" : "Aucun fond d'écran mis en cache pour cette session";
    public ImageSource? Thumbnail { get => _thumbnail; private set => Set(ref _thumbnail, value); }

    public bool Include
    {
        get => Main.Selection.IncludeWallpaper && Exists;
        set
        {
            Main.Selection.IncludeWallpaper = value;
            OnPropertyChanged();
            UpdateSidebar();
            Main.RequestRecalc();
        }
    }

    public override void OnActivated()
    {
        if (_thumbnail != null || !Exists) return;
        var path = Info!.Path;
        Task.Run(() =>
        {
            // The cache file has no extension: decode it from a shared-read stream (it may be in use by Explorer).
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
        Status = !Exists ? "Non détecté" : Include ? "Inclus" : "Non inclus";
        IsDone = Include;
    }
}

// ====================================================================== Browsers

public sealed class BrowserProfileViewModel(BrowserProfileInfo info, BrowserCardViewModel owner) : ObservableObject
{
    public BrowserProfileInfo Info { get; } = info;
    public string DisplayName => Info.DisplayName;
    public string Folder => Info.FolderName;
    public string Account => Info.Account ?? "";
    public bool IsActive => Info.IsActive;

    public bool IsSelected
    {
        get => owner.Model.SelectedProfiles.Contains(Info.FullPath);
        set
        {
            if (value) owner.Model.SelectedProfiles.Add(Info.FullPath);
            else owner.Model.SelectedProfiles.Remove(Info.FullPath);
            OnPropertyChanged();
            owner.SelectionChanged();
        }
    }
}

public sealed class BrowserCardViewModel : ObservableObject
{
    private readonly BrowsersViewModel _owner;
    private bool _isRunning;
    private string _sizeText = "";

    public BrowserCardViewModel(BrowserSelection model, BrowsersViewModel owner)
    {
        Model = model;
        _owner = owner;
        Profiles = new ObservableCollection<BrowserProfileViewModel>(model.Info.Profiles.Select(p => new BrowserProfileViewModel(p, this)));
        RefreshRunning();
    }

    public BrowserSelection Model { get; }
    public string Name => Model.Info.Name;
    public string ShortName => Model.Info.Kind switch { BrowserKind.Chrome => "Chrome", BrowserKind.Edge => "Edge", _ => "Firefox" };
    public bool Detected => Model.Info.Detected;
    public string DataRoot => Model.Info.DataRoot;
    public string? Method => Model.Info.ResolutionMethod;
    public string? Warning => Model.Info.Warning;
    public ObservableCollection<BrowserProfileViewModel> Profiles { get; }
    public string AutomationId => "Browser_" + ShortName;

    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(RunningText)); } }

    public string RunningText =>
        $"{Name} est actuellement ouvert. Au lancement de la copie, {AppInfo.Name} proposera de le fermer complètement " +
        $"(après confirmation) afin d'obtenir une sauvegarde complète du profil.";

    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }

    public string SelectionText => !Detected ? "Non détecté sur cette session"
        : Profiles.Count == 0 ? "Aucun profil"
        : $"{Model.SelectedProfiles.Count} / {Profiles.Count} profil(s) sélectionné(s)";

    // Same scope as the closing (current Windows session only), so "open" here means "will be closed at launch".
    public void RefreshRunning() => IsRunning = Detected && BrowserProcessService.IsRunning(Model.Info.Kind);

    public void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionText));
        _owner.SelectionChanged();
    }
}

public sealed class BrowsersViewModel : PageViewModel
{
    private DateTime _lastRunningCheck = DateTime.MinValue;

    public BrowsersViewModel(MainViewModel main) : base(main, 5, "Navigateurs", "", "Nav_Browsers")
    {
        Cards = new ObservableCollection<BrowserCardViewModel>(main.Selection.Browsers.Select(b => new BrowserCardViewModel(b, this)));
        RefreshCommand = new RelayCommand(() => { foreach (var c in Cards) c.RefreshRunning(); OnPropertyChanged(nameof(AnyRunning)); });
        UpdateSidebar();
    }

    public ObservableCollection<BrowserCardViewModel> Cards { get; }
    public ICommand RefreshCommand { get; }
    public bool AnyRunning => Cards.Any(c => c.IsRunning);
    public IEnumerable<BrowserCardViewModel> RunningBrowsers => Cards.Where(c => c.IsRunning);

    public void SelectionChanged()
    {
        UpdateSidebar();
        Main.RequestRecalc();
    }

    public void ApplyTotals(PlanTotals totals)
    {
        foreach (var card in Cards)
        {
            var jobs = totals.PerJob.Where(p => p.Key.Category == Categories.Browsers && p.Key.Label.StartsWith(card.Name, StringComparison.Ordinal)).ToList();
            if (jobs.Count == 0) { card.SizeText = ""; continue; }
            var sum = jobs.Aggregate(Totals.Zero, (acc, p) => acc + p.Value);
            card.SizeText = sum.Complete ? $"≈ {Format.Size(sum.Bytes)} (caches exclus)" : "Calcul…";
        }
    }

    public override void OnTick()
    {
        if (DateTime.Now - _lastRunningCheck < TimeSpan.FromSeconds(3)) return;
        _lastRunningCheck = DateTime.Now;
        foreach (var c in Cards) c.RefreshRunning();
        OnPropertyChanged(nameof(AnyRunning));
    }

    private void UpdateSidebar()
    {
        var included = Cards.Where(c => c.Model.Include).Select(c => c.ShortName).ToList();
        Status = included.Count == 0 ? "Aucun" : string.Join(", ", included);
        IsDone = included.Count > 0;
    }
}

// ====================================================================== Wi-Fi

public sealed class WifiItemViewModel(WifiProfileInfo info, WifiViewModel owner) : ObservableObject
{
    public WifiProfileInfo Info { get; } = info;
    public string Name => Info.Name;
    public string Authentication => Info.Authentication;
    public string Scope => Info.ScopeLabel;
    public string KeyText => Info.KeyLabel;
    public bool KeyAvailable => Info.KeyStatus == WifiKeyStatus.Available;
    public bool KeyBlocked => Info.KeyStatus is WifiKeyStatus.NotAccessible or WifiKeyStatus.Unknown;
    public bool NoKey => Info.KeyStatus == WifiKeyStatus.NoKey;

    public bool IsSelected
    {
        get => owner.Selection.SelectedWifi.Contains(Info.Name);
        set
        {
            if (value) owner.Selection.SelectedWifi.Add(Info.Name);
            else owner.Selection.SelectedWifi.Remove(Info.Name);
            OnPropertyChanged();
            owner.SelectionChanged();
        }
    }
}

public sealed class WifiViewModel : PageViewModel
{
    private bool _isLoaded;
    private bool _loadedOnce;
    private bool _serviceAvailable;
    private string? _message;

    public WifiViewModel(MainViewModel main) : base(main, 6, "Réseaux Wi-Fi", "", "Nav_Wifi")
    {
        RefreshCommand = new RelayCommand(Load);
        SelectAllCommand = new RelayCommand(() => SetAll(true));
        SelectNoneCommand = new RelayCommand(() => SetAll(false));
        Status = "Détection…";
    }

    public BackupSelection Selection => Main.Selection;
    public ObservableCollection<WifiItemViewModel> Items { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }
    public bool ServiceAvailable { get => _serviceAvailable; private set => Set(ref _serviceAvailable, value); }
    public string? Message { get => _message; private set => Set(ref _message, value); }
    public int KeyBlockedCount => Items.Count(i => i.KeyBlocked);

    public void Load()
    {
        IsLoaded = false;
        Status = "Détection…";
        Task.Run(WifiService.Detect).ContinueWith(t =>
        {
            var detection = t.IsCompletedSuccessfully ? t.Result : new WifiDetection(false, "Détection Wi-Fi impossible : " + t.Exception?.InnerException?.Message, []);
            var previous = Selection.SelectedWifi.ToHashSet();
            Selection.WifiProfiles.Clear();
            Selection.WifiProfiles.AddRange(detection.Profiles);
            Selection.SelectedWifi.Clear();
            var first = !_loadedOnce;
            _loadedOnce = true;
            foreach (var p in detection.Profiles)
                if (first || previous.Contains(p.Name)) Selection.SelectedWifi.Add(p.Name);
            Items.Clear();
            foreach (var p in detection.Profiles) Items.Add(new WifiItemViewModel(p, this));
            ServiceAvailable = detection.ServiceAvailable;
            Message = detection.Message;
            IsLoaded = true;
            OnPropertyChanged(nameof(KeyBlockedCount));
            UpdateSidebar();
            Main.Home.Refresh();
            Main.RequestRecalc();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetAll(bool value)
    {
        foreach (var item in Items) item.IsSelected = value;
    }

    public void SelectionChanged()
    {
        UpdateSidebar();
        Main.RequestRecalc();
    }

    private void UpdateSidebar()
    {
        var n = Selection.SelectedWifi.Count;
        Status = !ServiceAvailable ? "Indisponible" : Items.Count == 0 ? "Aucun profil" : $"{n} / {Items.Count} profil(s)";
        IsDone = n > 0;
    }
}
