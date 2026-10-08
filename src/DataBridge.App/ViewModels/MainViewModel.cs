using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Profile;
using DataBridge.Core.Scanning;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

/// <summary>
/// Backup wizard: detection when the backup mode is chosen, navigation between sections, background size estimate
/// of the current selection (never blocking the UI), and the copy launch.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable, IExplorerHost
{
    private readonly DispatcherTimer _timer;
    private readonly ShellViewModel _shell;
    private WizardPage _currentPage;
    private bool _recalcRequested = true;
    private bool _recalcRunning;
    private PlanTotals? _totals;
    private bool _isCopying;
    private string _scanStatus = "";
    private DestinationConflict? _conflict;
    private bool _isLaunching;

    public MainViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Profile = ProfileInfo.Detect();
        OneDrive = OneDriveAccountService.Detect();
        OneDriveRoots = OneDrive.Roots;
        Scanner = new SizeScanner();
        Listings = new DirectoryListingCache();
        Calculator = new PlanCalculator(Scanner, Listings);

        Selection = new BackupSelection { Profile = Profile, OneDrive = OneDrive };
        foreach (var folder in PersonalFolderInfo.DetectAll(OneDriveRoots, TestOverrides()))
            Selection.PersonalRoots.Add(new PersonalRoot(folder));
        Selection.Signatures = SignatureInfo.Detect(Profile.RoamingAppData);
        Selection.IncludeSignatures = Selection.Signatures.Exists;
        Selection.Wallpaper = WallpaperInfo.Detect(Profile.RoamingAppData);
        Selection.IncludeWallpaper = Selection.Wallpaper.Exists;
        foreach (var browser in BrowserDetector.DetectAll(Profile.LocalAppData, Profile.RoamingAppData))
            Selection.Browsers.Add(new BrowserSelection(browser));

        Home = new HomeViewModel(this);
        Destination = new DestinationViewModel(this);
        Personal = new PersonalViewModel(this, Selection.PersonalRoots.Select(r => new PersonalRootViewModel(r)).ToList(), 2, "Nav_Personal",
            "Dépliez un dossier pour choisir précisément ce qui sera copié. Rien n'est exclu automatiquement : les fichiers volumineux sont seulement signalés.");
        Additional = new AdditionalViewModel(this);
        Outlook = new OutlookViewModel(this);
        Browsers = new BrowsersViewModel(this);
        Wifi = new WifiViewModel(this);
        Wallpaper = new WallpaperViewModel(this);
        Printers = new PrintersViewModel(this);
        Summary = new SummaryViewModel(this);
        Copy = new CopyViewModel(this);
        Pages = [Home, Destination, Personal, Additional, Outlook, Browsers, Wifi, Wallpaper, Printers, Summary, Copy];
        Copy.IsEnabled = false;
        _currentPage = Home;

        // Start sizing every personal folder in the background right away (progressively, UI stays responsive).
        foreach (var root in Selection.PersonalRoots.Where(r => r.Available))
            Scanner.Enqueue(root.Path);

        NextCommand = new RelayCommand(GoNext, () => !IsCopying && !IsLaunching && Pages.IndexOf(CurrentPage) < Pages.IndexOf(Summary));
        PreviousCommand = new RelayCommand(GoPrevious, () => !IsCopying && !IsLaunching && Pages.IndexOf(CurrentPage) > 0);
        HomeCommand = new RelayCommand(() => _shell.GoHome(), () => CanGoHome);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Wifi.Load();
        Printers.Load();
        Tick();
    }

    public ProfileInfo Profile { get; }
    /// <summary>OneDrive accounts of the session, identified from local metadata only.</summary>
    public OneDriveDetection OneDrive { get; }
    public IReadOnlyList<OneDriveRoot> OneDriveRoots { get; }
    public SizeScanner Scanner { get; }
    public DirectoryListingCache Listings { get; }
    public PlanCalculator Calculator { get; }
    public BackupSelection Selection { get; }

    public HomeViewModel Home { get; }
    public DestinationViewModel Destination { get; }
    public PersonalViewModel Personal { get; }
    public AdditionalViewModel Additional { get; }
    public OutlookViewModel Outlook { get; }
    public BrowsersViewModel Browsers { get; }
    public WifiViewModel Wifi { get; }
    public WallpaperViewModel Wallpaper { get; }
    public PrintersViewModel Printers { get; }
    public SummaryViewModel Summary { get; }
    public CopyViewModel Copy { get; }
    public ObservableCollection<WizardPage> Pages { get; }

    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    /// <summary>Back to the DataBridge home (Sauvegarder / Restaurer), never during a copy.</summary>
    public ICommand HomeCommand { get; }
    public bool CanGoHome => !IsCopying && !IsLaunching;
    /// <summary>The copy is over: the wizard is locked on its result and is rebuilt next time.</summary>
    public bool IsFinished { get; private set; }

    // Footer of the shared wizard view.
    public string FooterDetail => Destination.Status;
    public ICommand StartCommand => Summary.StartCommand;
    public string StartLabel => "Lancer la copie";
    public string StartAutomationId => "FooterStartCopyButton";
    public string ModeLabel => "Sauvegarde";
    public string StepsTitle => "ÉTAPES DE LA SAUVEGARDE";

    // IExplorerHost (personal folders explorer)
    bool IExplorerHost.SkipCloudOnly => Selection.SkipCloudOnly;
    void IExplorerHost.ExplorerSelectionChanged()
    {
        Additional.RefreshStatuses();
        RequestRecalc();
    }

    /// <summary>The wizard is shown again (timer resumed).</summary>
    public void Activate()
    {
        _timer.Start();
        Tick();
    }

    /// <summary>The wizard is hidden behind the home screen: its timer is paused, the selection is kept.</summary>
    public void Deactivate() => _timer.Stop();

    public string WindowTitle => $"{AppInfo.Name} — Sauvegarde du profil {Profile.UserName} sur {Profile.MachineName}";

    public string VersionText => $"{AppInfo.Name} {System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? ""}".Trim();

    /// <summary>Between the click on "Lancer la copie" and the actual start (browser confirmation and closing).</summary>
    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!Set(ref _isLaunching, value)) return;
            OnPropertyChanged(nameof(CanGoHome));
            Summary.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public WizardPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is null || ReferenceEquals(value, _currentPage)) return;
            if (IsCopying && value != Copy) { OnPropertyChanged(); return; }
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSummaryPage));
            OnPropertyChanged(nameof(ShowFooter));
            value.OnActivated();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSummaryPage => CurrentPage == Summary;
    public bool ShowFooter => CurrentPage != Copy;

    public bool IsCopying
    {
        get => _isCopying;
        set
        {
            if (!Set(ref _isCopying, value)) return;
            foreach (var p in Pages) p.IsEnabled = !value || p == Copy;
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(CanGoHome));
        }
    }

    // ------------------------------------------------------------------ totals

    public PlanTotals? Totals => _totals;
    public bool TotalsComplete => _totals?.Sum.Complete == true && !_recalcRequested;
    /// <summary>Selected / planned / cloud-only skipped, for the current cloud option.</summary>
    public CopyForecast? Forecast => _totals?.Sum.Forecast(Selection.SkipCloudOnly);

    public string FooterSelection
    {
        get
        {
            if (Forecast is not { } f) return "Calcul de la sélection…";
            var text = $"À copier : {Format.Size(f.PlannedCopyBytes)} · {Format.Plural(f.PlannedCopyFileCount, "fichier", "fichiers")}";
            return f.Complete ? text : text + " (calcul en cours…)";
        }
    }

    /// <summary>Cloud-only files selected but left online: shown apart, never in the "à copier" figures.</summary>
    public string FooterCloud => Forecast is { SkippedCloudOnlyCount: > 0 } f
        ? $"{Format.Count(f.SkippedCloudOnlyCount)} en ligne non téléchargés"
        : "";

    /// <summary>The backup folder would be inside a selected source (recursive copy): the launch is blocked.</summary>
    public DestinationConflict? Conflict
    {
        get => _conflict;
        private set
        {
            if (_conflict == value) return;
            _conflict = value;
            OnPropertyChanged();
            Destination.UpdateConflict();
            Summary.Refresh();
        }
    }

    /// <summary>Backup folder that the copy would create now (null while no destination is chosen).</summary>
    public string? BackupRootPreview => Destination.ChosenPath is { } d
        ? Path.Combine(d, BackupNaming.FolderName(Profile.MachineName, Profile.UserName, DateTime.Now))
        : null;

    /// <summary>Re-reads the cloud option (footer and summary figures change without rescanning).</summary>
    public void NotifyCloudOptionChanged()
    {
        OnPropertyChanged(nameof(Forecast));
        OnPropertyChanged(nameof(FooterSelection));
        OnPropertyChanged(nameof(FooterCloud));
    }

    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    /// <summary>Marks the selection as changed: the estimate is recomputed in the background.</summary>
    public void RequestRecalc()
    {
        _recalcRequested = true;
        OnPropertyChanged(nameof(TotalsComplete));
    }

    private void Tick()
    {
        ScanStatus = Scanner.IsBusy ? $"Analyse des tailles en cours… {Format.Count(Scanner.FilesSeen)} fichiers" : "";

        if (!_recalcRunning && (_recalcRequested || _totals is null || !_totals.Sum.Complete))
            StartRecalc();

        CurrentPage.OnTick();
        Personal.UpdateRootStatus();
        Destination.UpdateSidebar();
        OnPropertyChanged(nameof(FooterDetail));
    }

    private void StartRecalc()
    {
        _recalcRunning = true;
        _recalcRequested = false;
        var plan = BuildPlan(previewOnly: true);
        var destinationKnown = Destination.SelectedPath != null;
        Task.Run(() => (Totals: Calculator.ForPlan(plan, blocking: false), Conflict: destinationKnown ? DestinationGuard.Check(plan) : null)).ContinueWith(t =>
        {
            _recalcRunning = false;
            if (t.IsCompletedSuccessfully)
            {
                _totals = t.Result.Totals;
                OnPropertyChanged(nameof(Totals));
                OnPropertyChanged(nameof(TotalsComplete));
                NotifyCloudOptionChanged();
                Personal.ApplyTotals(_totals);
                Additional.ApplyTotals(_totals);
                Browsers.ApplyTotals(_totals);
                Outlook.ApplyTotals(_totals);
                Conflict = t.Result.Conflict;
                Summary.Refresh();
            }
            else
            {
                _recalcRequested = true;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Builds the copy plan for the current selection. The preview uses a placeholder backup folder name.</summary>
    public CopyPlan BuildPlan(bool previewOnly, string? folderName = null)
    {
        var destination = Destination.SelectedPath ?? @"Z:\DataBridge-preview";
        var name = folderName ?? BackupNaming.FolderName(Profile.MachineName, Profile.UserName, DateTime.Now);
        return Selection.BuildPlan(destination, name);
    }

    // ------------------------------------------------------------------ navigation

    private void GoNext()
    {
        var i = Pages.IndexOf(CurrentPage);
        if (i < Pages.IndexOf(Summary)) CurrentPage = Pages[i + 1];
    }

    private void GoPrevious()
    {
        var i = Pages.IndexOf(CurrentPage);
        if (i > 0) CurrentPage = Pages[i - 1];
    }

    public void GoTo(WizardPage page) => CurrentPage = page;

    // ------------------------------------------------------------------ copy

    public async void StartCopy()
    {
        // A second click while the browsers are being closed must not start a second copy.
        if (IsCopying || IsLaunching || Destination.SelectedPath is null || _totals is null) return;
        IsLaunching = true;
        try
        {
            var destination = Destination.SelectedPath;
            var name = BackupNaming.UniqueFolderName(destination, BackupNaming.FolderName(Profile.MachineName, Profile.UserName, DateTime.Now));
            var plan = Selection.BuildPlan(destination, name);

            // 1-2. Destination and free space are enforced by the summary (CanStart). Re-check the recursive-destination
            // guard on the exact plan (the selection may have changed since the last background pass).
            if (DestinationGuard.Check(plan) is { } conflict)
            {
                Conflict = conflict;
                CurrentPage = Summary;
                Views.DataBridgeDialog.ShowDestinationConflict(Application.Current.MainWindow, conflict);
                return;
            }

            // 3-7. Close the selected browsers that are open, after an explicit confirmation. Abort the copy on cancel.
            if (!await EnsureSelectedBrowsersClosedAsync(plan)) return;

            Copy.IsEnabled = true;
            IsCopying = true;
            CurrentPage = Copy;
            Copy.Start(plan);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    /// <summary>
    /// Browsers whose profile is really going to be copied AND currently open: confirm, then fully close them
    /// (process tree, current session only) before the copy. Returns false when the user cancels. The timestamped
    /// events are attached to the plan so they appear in copy-log.txt.
    /// </summary>
    private async Task<bool> EnsureSelectedBrowsersClosedAsync(CopyPlan plan)
    {
        var selected = Browsers.Cards.Where(c => c.Model.Include).Select(c => c.Model.Info.Kind).ToList();
        var open = selected.Where(BrowserProcessService.IsRunning).ToList();
        if (open.Count == 0) return true; // nothing selected is open: nothing to close

        var owner = Application.Current.MainWindow;
        if (!Views.DataBridgeDialog.ConfirmBrowserClose(owner, open))
            return false; // 13. Cancel: close nothing, copy nothing, stay on the summary

        var events = new List<string>();
        string Now() => DateTime.Now.ToString("HH:mm:ss");
        foreach (var kind in open) events.Add($"[{Now()}] {BrowserProcessService.DisplayName(kind)} détecté.");
        events.Add($"[{Now()}] Fermeture forcée confirmée par l'utilisateur.");

        var toClose = open;
        while (true)
        {
            var results = await BrowserProcessService.CloseAsync(toClose);
            foreach (var r in results.Where(r => r.Closed))
                events.Add($"[{Now()}] {r.Name} fermé.");

            var remaining = results.Where(r => !r.Closed).Select(r => r.Kind).ToList();
            if (remaining.Count == 0) break;

            foreach (var r in results.Where(r => !r.Closed))
                events.Add($"[{Now()}] {r.Name} : {r.RemainingProcesses} processus toujours actif(s).");

            if (!Views.DataBridgeDialog.RetryBrowserClose(owner, remaining))
            {
                plan.BrowserEvents = [.. events, $"[{Now()}] Sauvegarde annulée : navigateurs non fermés."];
                return false; // 22. Cancel on failure: never elevate, never copy
            }
            toClose = remaining;
        }

        events.Add($"[{Now()}] Copie des profils navigateur démarrée.");
        plan.BrowserEvents = events;
        return true;
    }

    public void CopyFinished()
    {
        IsCopying = false;
        IsFinished = true;
        foreach (var p in Pages) p.IsEnabled = p == Copy; // the backup is done: the wizard stays on the result
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>"Terminer" on the result: back to the DataBridge home (the application is not closed).</summary>
    public void Finish() => _shell.GoHome();

    public void Dispose()
    {
        _timer.Stop();
        Scanner.Dispose();
    }

    private static IReadOnlyDictionary<PersonalFolderKind, string>? TestOverrides()
    {
#if DEBUG
        // Debug builds only: DATABRIDGE_TEST_DESKTOP=<path> (etc.) simulates a redirected Known Folder for tests.
        var map = new Dictionary<PersonalFolderKind, string>();
        foreach (var kind in Enum.GetValues<PersonalFolderKind>())
        {
            var value = Environment.GetEnvironmentVariable("DATABRIDGE_TEST_" + kind.ToString().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(value)) map[kind] = value;
        }
        return map.Count > 0 ? map : null;
#else
        return null;
#endif
    }

    public static void ShowError(string message) => Views.DataBridgeDialog.ShowError(Application.Current.MainWindow, message);
}
