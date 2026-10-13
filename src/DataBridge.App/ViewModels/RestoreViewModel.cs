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
using DataBridge.Core.Restore;
using DataBridge.Core.Scanning;

namespace DataBridge.App.ViewModels;

/// <summary>
/// Restore wizard: 1. Sauvegarde … 9. Restauration. The pages only edit a <see cref="RestoreSelection"/>; the plan, its
/// validation and its analysis come from <see cref="RestorePlanner"/> (Core), and the engine executes that same plan.
/// </summary>
public sealed class RestoreViewModel : ObservableObject, IDisposable, IExplorerHost
{
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _timer;
    private WizardPage _currentPage;
    private RestoreSelection? _selection;
    private bool _isRestoring;
    private bool _isLaunching;
    private bool _isFinished;
    private string _scanStatus = "";

    // Size estimate (cached sizes of the backup, never blocking).
    private bool _recalcRequested;
    private bool _recalcRunning;
    private PlanTotals? _totals;

    // Analysis against the destination (conflicts, space), refreshed for the summary.
    private int _version;
    private int _analysisVersion = -1;
    private CancellationTokenSource? _analysisCts;
    private RestoreAnalysis? _analysis;
    private RestorePlan? _analysisPlan;
    private bool _analysisRunning;
    private long _analysisSeen;

    public RestoreViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Target = RestoreTarget.Detect(TestOverrides());
        Scanner = new SizeScanner();
        Listings = new DirectoryListingCache();
        Calculator = new PlanCalculator(Scanner, Listings);

        Source = new RestoreSourceViewModel(this);
        Personal = EmptyPersonal();
        Additional = new RestoreAdditionalViewModel(this);
        Outlook = new RestoreOutlookViewModel(this);
        Browsers = new RestoreBrowsersViewModel(this);
        Wifi = new RestoreWifiViewModel(this);
        NetworkDrives = new RestoreNetworkDrivesViewModel(this);
        Wallpaper = new RestoreWallpaperViewModel(this);
        Printers = new RestorePrintersViewModel(this);
        Summary = new RestoreSummaryViewModel(this);
        Progress = new RestoreProgressViewModel(this);
        Hub = new RestoreHubViewModel(this);
        Pages = [Source, Hub, Personal, Additional, Outlook, Browsers, Wifi, NetworkDrives, Wallpaper, Printers, Summary, Progress];
        foreach (var p in Pages) p.IsEnabled = p == Source;
        _currentPage = Source;
        Steps =
        [
            new WizardStep(1, "Restaurer", "R_Step_Mode", () => _shell.GoHome()),
            new WizardStep(2, "Sauvegarde", "R_Nav_Source", () => GoTo(Source)),
            new WizardStep(3, "Quoi", "R_Step_What", () => GoTo(Hub)),
            new WizardStep(4, "Lancer", "R_Nav_Summary", () => GoTo(Summary)),
        ];
        UpdateSteps();

        // Main path: Sauvegarde (Source) → Quoi (Hub) → Lancer (Summary). A detailed page always comes back to Quoi.
        NextCommand = new RelayCommand(GoNext, () => !IsBusy && CurrentPage != Summary && (HasBackup || Source.HasContent));
        PreviousCommand = new RelayCommand(GoPrevious, () => !IsBusy && !IsFinished);
        BackToHubCommand = new RelayCommand(() => GoTo(Hub), () => IsDetailPage && !IsBusy && !IsFinished);
        HomeCommand = new RelayCommand(() => _shell.GoHome(), () => CanGoHome);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public RestoreTarget Target { get; private set; }
    public ProfileInfo Profile => Target.Profile;
    public SizeScanner Scanner { get; }
    public DirectoryListingCache Listings { get; }
    public PlanCalculator Calculator { get; }
    public RestoreSelection? Selection => _selection;
    public BackupContent? Backup => _selection?.Backup;
    public bool HasBackup => _selection != null;
    public string BackupName => Backup is { } b ? Path.GetFileName(b.Root) : "";

    public RestoreSourceViewModel Source { get; }
    /// <summary>Step 3 "Quoi": the categories of the backup on one page; the detailed pages below are opened from its tiles.</summary>
    public RestoreHubViewModel Hub { get; }
    public PersonalViewModel Personal { get; private set; }
    public RestoreAdditionalViewModel Additional { get; }
    public RestoreOutlookViewModel Outlook { get; }
    public RestoreBrowsersViewModel Browsers { get; }
    public RestoreWifiViewModel Wifi { get; }
    public RestoreNetworkDrivesViewModel NetworkDrives { get; }
    public RestoreWallpaperViewModel Wallpaper { get; }
    public RestorePrintersViewModel Printers { get; }
    public RestoreSummaryViewModel Summary { get; }
    public RestoreProgressViewModel Progress { get; }
    public ObservableCollection<WizardPage> Pages { get; }
    /// <summary>The four steps shown at the top of the window.</summary>
    public IReadOnlyList<WizardStep> Steps { get; }

    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    /// <summary>"← Retour à Quoi" of a "Personnaliser" sub-page: every choice made there is kept (same selection objects).</summary>
    public ICommand BackToHubCommand { get; }
    public ICommand HomeCommand { get; }

    public string WindowTitle => $"{AppInfo.Name} — Restauration d'une sauvegarde sur {Profile.MachineName}";
    public string VersionText => $"{AppInfo.Name} {AppInfo.Version}";
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }

    public bool IsRestoring
    {
        get => _isRestoring;
        private set
        {
            if (!Set(ref _isRestoring, value)) return;
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(CanGoHome));
            UpdateSteps();
        }
    }

    /// <summary>Between the click on "Lancer la restauration" and the start (browser confirmation and closing).</summary>
    public bool IsLaunching
    {
        get => _isLaunching;
        private set
        {
            if (!Set(ref _isLaunching, value)) return;
            OnPropertyChanged(nameof(CanGoHome));
            UpdateSteps();
            Summary.Refresh();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsFinished { get => _isFinished; private set => Set(ref _isFinished, value); }
    public bool IsBusy => IsRestoring || IsLaunching;
    public bool CanGoHome => !IsBusy;

    public WizardPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (value is null || ReferenceEquals(value, _currentPage)) return;
            if ((IsRestoring && value != Progress) || !value.IsEnabled) { OnPropertyChanged(); return; }
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSummaryPage));
            OnPropertyChanged(nameof(ShowFooter));
            OnPropertyChanged(nameof(IsDetailPage));
            OnPropertyChanged(nameof(NextLabel));
            UpdateSteps();
            value.OnActivated();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsSummaryPage => CurrentPage == Summary;
    /// <summary>A page opened from a tile of step 3 ("Personnaliser"): its footer only brings back to step 3.</summary>
    public bool IsDetailPage => CurrentPage != Source && CurrentPage != Hub && CurrentPage != Summary && CurrentPage != Progress;
    public string NextLabel => IsDetailPage ? "Terminé" : "Suivant";
    public bool ShowFooter => CurrentPage != Progress;

    // ------------------------------------------------------------------ footer (shared wizard view)

    public string FooterSelection
    {
        get
        {
            if (!HasBackup) return "Aucune sauvegarde sélectionnée";
            if (_analysis != null && _analysisVersion == _version)
                return $"À restaurer : {Format.Size(_analysis.WriteBytes)} · {Format.Plural(_analysis.WriteFiles, "fichier", "fichiers")}";
            if (_totals is not { } t) return "Calcul de la sélection…";
            var text = $"Sélection : {Format.Size(t.Sum.Bytes)} · {Format.Plural(t.Sum.Files, "fichier", "fichiers")}";
            return t.Sum.Complete ? text : text + " (calcul en cours…)";
        }
    }

    public string FooterCloud => "";
    public string FooterDetail => HasBackup ? "Sauvegarde : " + BackupName : "Choisissez une sauvegarde DataBridge";
    public ICommand StartCommand => Summary.StartCommand;
    public string StartLabel => "Lancer la restauration";
    public string StartAutomationId => "FooterStartRestoreButton";

    public PlanTotals? Totals => _totals;

    // IExplorerHost
    bool IExplorerHost.SkipCloudOnly => true;
    void IExplorerHost.ExplorerSelectionChanged() => SelectionChanged();

    // ------------------------------------------------------------------ backup

    /// <summary>Uses the backup read by page 1: builds the default selection and unlocks the pages.</summary>
    public void LoadBackup(BackupContent content)
    {
        if (_selection?.Backup.Root == content.Root && _selection.Backup == content)
        {
            GoTo(Hub);
            return;
        }
        Target = Target.WithFreshBrowsers();
        _selection = RestoreSelection.Create(content, Target);
        _selection.WifiState = WifiRestoreService.Inspect();
        _selection.NetworkDriveState = NetworkDriveService.Inspect();
        Scanner.Invalidate();
        Listings.Invalidate();

        var roots = _selection.Personal.Select(p => new PersonalRootViewModel(p.Root, p)).ToList();
        var index = Pages.IndexOf(Personal);
        Personal = new PersonalViewModel(this, roots, 2, "R_Nav_Personal",
            "Dossiers de la sauvegarde restaurés dans les dossiers personnels de cette session (Known Folders actuels). " +
            "Dépliez un dossier pour choisir précisément ce qui sera restauré.", isRestore: true);
        Pages[index] = Personal;
        foreach (var root in roots.Where(r => r.Available)) Scanner.Enqueue(root.Model.Path);

        Additional.Load();
        Outlook.Load();
        Browsers.Load();
        Wifi.Load();
        NetworkDrives.Load();
        Wallpaper.Load();
        Printers.Load();
        foreach (var p in Pages) p.IsEnabled = p != Progress;
        Source.MarkUsed();
        OnPropertyChanged(nameof(HasBackup));
        OnPropertyChanged(nameof(BackupName));
        OnPropertyChanged(nameof(FooterDetail));
        SelectionChanged();
        Personal.RefreshStates();
        Hub.Load();
        GoTo(Hub);
    }

    private PersonalViewModel EmptyPersonal() =>
        new(this, [], 2, "R_Nav_Personal", "Choisissez d'abord une sauvegarde.", isRestore: true);

    /// <summary>The selection changed somewhere: estimate and analysis are recomputed in the background.</summary>
    public void SelectionChanged()
    {
        _version++;
        _recalcRequested = true;
        _analysisCts?.Cancel();
        OnPropertyChanged(nameof(FooterSelection));
        Summary.Refresh();
    }

    public RestorePlan BuildPlan() => RestorePlanner.BuildPlan(_selection!);

    // ------------------------------------------------------------------ timer, estimate, analysis

    private void Tick()
    {
        ScanStatus = Scanner.IsBusy ? $"Analyse des tailles… {Format.Count(Scanner.FilesSeen)} fichiers" : "";
        if (HasBackup && !_recalcRunning && (_recalcRequested || _totals is null || !_totals.Sum.Complete)) StartRecalc();
        if (HasBackup && CurrentPage == Summary && !_analysisRunning && _analysisVersion != _version && !IsBusy) StartAnalysis();
        CurrentPage.OnTick();
        if (HasBackup) Personal.UpdateRootStatus();
        OnPropertyChanged(nameof(FooterSelection));
    }

    private void StartRecalc()
    {
        _recalcRunning = true;
        _recalcRequested = false;
        var jobs = RestorePlanner.EstimateJobs(BuildPlan());
        var plan = new CopyPlan { Profile = Profile, DestinationBase = @"Z:\estimate", BackupFolderName = "x", Jobs = jobs };
        Task.Run(() => Calculator.ForPlan(plan, blocking: false)).ContinueWith(t =>
        {
            _recalcRunning = false;
            if (!t.IsCompletedSuccessfully) { _recalcRequested = true; return; }
            _totals = t.Result;
            Personal.ApplyTotals(_totals);
            Additional.ApplyTotals(_totals);
            OnPropertyChanged(nameof(Totals));
            OnPropertyChanged(nameof(FooterSelection));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public RestoreAnalysis? Analysis => _analysisVersion == _version ? _analysis : null;
    public RestorePlan? AnalysisPlan => _analysisVersion == _version ? _analysisPlan : null;
    public bool AnalysisRunning => _analysisRunning;
    public long AnalysisSeen => Interlocked.Read(ref _analysisSeen);

    private void StartAnalysis()
    {
        _analysisCts?.Cancel();
        var cts = new CancellationTokenSource();
        _analysisCts = cts;
        _analysisRunning = true;
        Interlocked.Exchange(ref _analysisSeen, 0);
        var version = _version;
        var plan = BuildPlan();
        Summary.Refresh();
        Task.Run(() => RestorePlanner.Analyze(plan, cts.Token, n => Interlocked.Exchange(ref _analysisSeen, n)), cts.Token).ContinueWith(t =>
        {
            _analysisRunning = false;
            if (t.IsCompletedSuccessfully && version == _version)
            {
                _analysis = t.Result;
                _analysisPlan = plan;
                _analysisVersion = version;
            }
            OnPropertyChanged(nameof(FooterSelection));
            Summary.Refresh();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------ navigation

    private void GoNext()
    {
        if (CurrentPage == Source)
        {
            // "Suivant" on the backup chosen (and checked) uses it, like "Utiliser cette sauvegarde".
            if (Source.Content is { } content && !Source.IsInUse) LoadBackup(content);
            else GoTo(Hub);
            return;
        }
        CurrentPage = CurrentPage == Hub ? Summary : Hub;
    }

    private void GoPrevious()
    {
        if (CurrentPage == Source) _shell.GoHome();
        else CurrentPage = CurrentPage == Hub ? Source : Hub;
    }

    public void GoTo(WizardPage page) => CurrentPage = page;

    private void UpdateSteps()
    {
        var current = CurrentPage == Source ? 2 : CurrentPage == Summary || CurrentPage == Progress ? 4 : 3;
        var locked = IsRestoring || IsLaunching || IsFinished;
        foreach (var s in Steps)
        {
            s.IsCurrent = s.Number == current;
            s.IsDone = s.Number < current || (IsFinished && s.Number == 4);
            s.CanGo = !locked && s.Number != current && (s.Number <= 2 || HasBackup);
        }
    }

    public void Activate()
    {
        _timer.Start();
        Tick();
    }

    public void Deactivate() => _timer.Stop();

    // ------------------------------------------------------------------ restore

    public async void StartRestore()
    {
        if (IsBusy || _selection is null) return;
        IsLaunching = true;
        try
        {
            var plan = BuildPlan();
            var issues = RestorePlanner.Validate(_selection, plan, Analysis);
            if (issues.FirstOrDefault(i => i.IsBlocking) is { } blocking)
            {
                CurrentPage = Summary;
                Views.DataBridgeDialog.ShowError(Application.Current.MainWindow, blocking.Message);
                return;
            }

            // Browsers whose profile is going to be written AND currently open: confirm, close, verify.
            var open = plan.BrowserKinds.Where(BrowserProcessService.IsRunning).ToList();
            if (open.Count > 0)
            {
                var events = await BrowserClosing.EnsureClosedAsync(open, restore: true);
                if (events is null) return; // cancelled: nothing closed beyond what the user accepted, nothing written
                plan.BrowserEvents = events;
            }

            // Owner of Windows' own credential prompt, shown if a network drive asks for its password.
            plan.OwnerWindow = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;

            Progress.IsEnabled = true;
            IsRestoring = true;
            foreach (var p in Pages) p.IsEnabled = p == Progress;
            CurrentPage = Progress;
            Progress.Start(plan);
        }
        finally
        {
            IsLaunching = false;
        }
    }

    public void RestoreFinished()
    {
        IsRestoring = false;
        IsFinished = true;
        foreach (var p in Pages) p.IsEnabled = p == Progress;
        UpdateSteps();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>"Terminer": back to the DataBridge home (the application stays open).</summary>
    public void Finish() => _shell.GoHome();

    public void Dispose()
    {
        _timer.Stop();
        _analysisCts?.Cancel();
        Printers.DisposeSource();
        Scanner.Dispose();
    }

    private static RestoreTargetOverrides? TestOverrides()
    {
#if DEBUG
        // Debug builds only (controlled tests): DATABRIDGE_TEST_DOCUMENTS=<path> etc. redirect the Known Folders,
        // DATABRIDGE_TEST_ROAMING / DATABRIDGE_TEST_LOCAL the application data, DATABRIDGE_TEST_NO_WALLPAPER_APPLY=1
        // keeps the desktop wallpaper unchanged. The Release build never reads these variables.
        var map = new Dictionary<PersonalFolderKind, string>();
        foreach (var kind in Enum.GetValues<PersonalFolderKind>())
        {
            var value = Environment.GetEnvironmentVariable("DATABRIDGE_TEST_" + kind.ToString().ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(value)) map[kind] = value;
        }
        string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        var roaming = Env("DATABRIDGE_TEST_ROAMING");
        var local = Env("DATABRIDGE_TEST_LOCAL");
        var noWallpaper = Env("DATABRIDGE_TEST_NO_WALLPAPER_APPLY") == "1";
        return map.Count > 0 || roaming != null || local != null || noWallpaper
            ? new RestoreTargetOverrides(map.Count > 0 ? map : null, roaming, local, noWallpaper)
            : null;
#else
        return null;
#endif
    }
}
