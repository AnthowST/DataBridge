using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Restore;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

public sealed record MappingLine(string Label, string Source, string Destination, string Note, bool IsOneDrive);

public sealed record VolumeLine(string Root, string Needed, string Free, bool Ok);

// ====================================================================== 8. Résumé

public sealed class RestoreSummaryViewModel : RestorePageViewModel
{
    private IReadOnlyList<SummaryGroup> _groups = [];
    private IReadOnlyList<MappingLine> _mappings = [];
    private IReadOnlyList<VolumeLine> _volumes = [];
    private IReadOnlyList<string> _issues = [];
    private IReadOnlyList<string> _warnings = [];

    public RestoreSummaryViewModel(RestoreViewModel restore) : base(restore, 8, "Résumé", "", "R_Nav_Summary")
    {
        StartCommand = new RelayCommand(() => Restore.StartRestore(), () => CanStart);
        Status = "Vérification avant restauration";
    }

    public ICommand StartCommand { get; }

    // Source / destination
    public string SourceComputer => Sel?.Backup.Source.Computer ?? "inconnu";
    public string SourceUser => Sel?.Backup.Source.User ?? "inconnu";
    public string SourceDate => Sel?.Backup.Source.CreatedAt?.ToString("dd.MM.yyyy HH:mm") ?? "inconnue";
    public string SourceVersion => Sel?.Backup.VersionLabel ?? "";
    public string Generation => Sel?.Backup.GenerationLabel ?? "";
    public bool IsLegacy => Sel?.Backup.IsLegacy == true;
    public string BackupPath => Sel?.Backup.Root ?? "";
    public string TargetComputer => Restore.Profile.MachineName;
    public string TargetUser => Restore.Profile.UserName;
    public string TargetProfile => Restore.Profile.ProfilePath;

    public IReadOnlyList<SummaryGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
    public IReadOnlyList<MappingLine> Mappings { get => _mappings; private set => Set(ref _mappings, value); }
    public IReadOnlyList<VolumeLine> Volumes { get => _volumes; private set => Set(ref _volumes, value); }
    public IReadOnlyList<string> Issues { get => _issues; private set => Set(ref _issues, value); }
    public IReadOnlyList<string> Warnings { get => _warnings; private set => Set(ref _warnings, value); }
    public bool HasIssues => _issues.Count > 0;
    public bool HasWarnings => _warnings.Count > 0;

    // Figures (analysis with the engine's rules)
    private RestoreAnalysis? A => Restore.Analysis;
    public bool IsAnalyzing => A is null;
    public string AnalysisText => Restore.AnalysisRunning
        ? $"Analyse des fichiers existants… {Format.Count(Restore.AnalysisSeen)} fichiers comparés"
        : "Analyse en attente…";
    public string FilesToRestoreText => A is { } a ? Format.Count(a.WriteFiles) : "…";
    public string VolumeText => A is { } a ? Format.Size(a.WriteBytes) : "Calcul…";
    public string SelectedText => A is { } a ? $"{Format.Plural(a.Files, "fichier sélectionné", "fichiers sélectionnés")} dans la sauvegarde ({Format.Size(a.Bytes)})" : "";
    public string NewText => A is { } a ? Format.Count(a.NewFiles) : "…";
    public string ExistingText => A is { } a ? Format.Count(a.ExistingFiles) : "…";
    public string ReplacedText => A is { } a ? Format.Count(a.ReplaceFiles) : "…";
    public string KeptText => A is { } a ? Format.Count(a.KeepFiles) : "…";
    public string IdenticalText => A is { } a ? Format.Count(a.IdenticalFiles) : "…";
    public string WifiText => A is { WifiCount: > 0 } a ? Format.Plural(a.WifiCount, "profil Wi-Fi", "profils Wi-Fi") : "";

    // Conflict policy (one global rule, no prompt per file)
    public bool PolicyReplace { get => Sel?.Policy == ConflictPolicy.Replace; set { if (value) SetPolicy(ConflictPolicy.Replace); } }
    public bool PolicyKeep { get => Sel?.Policy == ConflictPolicy.Keep; set { if (value) SetPolicy(ConflictPolicy.Keep); } }
    public bool PolicyNewer { get => Sel?.Policy is null or ConflictPolicy.ReplaceIfNewer; set { if (value) SetPolicy(ConflictPolicy.ReplaceIfNewer); } }
    public string PolicyText => Sel is null ? "" : Sel.Policy switch
    {
        ConflictPolicy.Replace => "Les fichiers déjà présents sont remplacés par ceux de la sauvegarde (sauf s'ils sont identiques).",
        ConflictPolicy.Keep => "Les fichiers déjà présents ne sont jamais modifiés : seuls les fichiers absents sont restaurés.",
        _ => "Les fichiers absents sont restaurés ; un fichier déjà présent n'est remplacé que si la sauvegarde est plus récente.",
    };
    public string PolicyNote => "Profils de navigateur et fond d'écran : remplacés, avec une copie de sécurité préalable des fichiers modifiés.";

    private void SetPolicy(ConflictPolicy policy)
    {
        if (Sel is null || Sel.Policy == policy) return;
        Sel.Policy = policy;
        OnPropertyChanged(nameof(PolicyReplace));
        OnPropertyChanged(nameof(PolicyKeep));
        OnPropertyChanged(nameof(PolicyNewer));
        OnPropertyChanged(nameof(PolicyText));
        Restore.SelectionChanged();
    }

    public bool CanStart => Restore.HasBackup && A != null && !HasIssues && !Restore.IsBusy && !Restore.IsFinished;
    public string BlockReason =>
        !Restore.HasBackup ? "Choisissez une sauvegarde."
        : Restore.IsLaunching ? "Préparation du lancement (fermeture des navigateurs)…"
        : A is null ? "Analyse des fichiers existants en cours…"
        : HasIssues ? "Corrigez les points bloquants ci-dessus avant de lancer la restauration."
        : "";

    public override void OnActivated()
    {
        foreach (var c in Restore.Browsers.Cards) c.RefreshRunning();
        Refresh();
    }

    public override void OnTick()
    {
        if (Restore.AnalysisRunning) OnPropertyChanged(nameof(AnalysisText));
    }

    public void Refresh()
    {
        if (Sel is not { } s)
        {
            Status = "Sauvegarde à choisir";
            return;
        }
        var plan = Restore.AnalysisPlan ?? Restore.BuildPlan();
        var analysis = A;

        string SizeOf(Func<RestoreItem, bool> match)
        {
            if (analysis is null) return "analyse…";
            var items = analysis.PerItem.Where(p => match(p.Key)).Select(p => p.Value).ToList();
            if (items.Count == 0) return "";
            var files = items.Sum(i => i.WriteFiles);
            return files == 0 ? "déjà à jour" : $"{Format.Plural(files, "fichier", "fichiers")} · {Format.Size(items.Sum(i => i.WriteBytes))} à écrire";
        }

        var groups = new List<SummaryGroup>();
        var personal = new List<SummaryLine>();
        foreach (var root in Restore.Personal.Roots)
        {
            if (!root.Available) { personal.Add(new SummaryLine(MarkKind.Off, root.Label, "absent de la sauvegarde")); continue; }
            if (root.Restore?.DestinationPath is null && root.State != CheckState.Unchecked) { personal.Add(new SummaryLine(MarkKind.Warn, root.Label, "destination introuvable")); continue; }
            var size = SizeOf(i => i.Category == RestoreCategories.Personal && i.Label == root.Label);
            personal.Add(root.State switch
            {
                CheckState.Checked => new SummaryLine(MarkKind.Ok, root.Label, size),
                CheckState.Indeterminate => new SummaryLine(MarkKind.Partial, root.Label, "sélection partielle · " + size),
                _ => new SummaryLine(MarkKind.Off, root.Label, "non inclus"),
            });
        }
        if (personal.Count == 0) personal.Add(new SummaryLine(MarkKind.Off, "Aucun dossier personnel", "absent de la sauvegarde"));
        groups.Add(new SummaryGroup("Dossiers personnels", personal));

        var additional = s.Additional.Select(a => !a.Selected ? new SummaryLine(MarkKind.Off, a.Item.Name, "non inclus")
            : !a.HasDestination ? new SummaryLine(MarkKind.Warn, a.Item.Name, "destination à choisir")
            : new SummaryLine(MarkKind.Ok, a.Item.Name, SizeOf(i => i.Category == RestoreCategories.Additional && i.Label == a.Item.Name))).ToList();
        if (additional.Count == 0) additional.Add(new SummaryLine(MarkKind.Off, "Aucun élément", "absent de la sauvegarde"));
        groups.Add(new SummaryGroup("Fichiers supplémentaires", additional));

        var apps = new List<SummaryLine>
        {
            s.Backup.Signatures is null ? new SummaryLine(MarkKind.Off, "Signatures Outlook", "absent de la sauvegarde")
            : s.IncludeSignatures ? new SummaryLine(MarkKind.Ok, "Signatures Outlook", $"{s.Backup.Signatures.Names.Count} · {SizeOf(i => i.Category == RestoreCategories.Outlook)}")
            : new SummaryLine(MarkKind.Off, "Signatures Outlook", "non incluses"),
        };
        foreach (var kind in new[] { BrowserKind.Chrome, BrowserKind.Edge, BrowserKind.Firefox })
        {
            var name = BrowserProcessService.DisplayName(kind).Split(' ')[^1];
            var choices = s.Browsers.Where(b => b.Browser.Kind == kind).ToList();
            if (choices.Count == 0) { apps.Add(new SummaryLine(MarkKind.Off, name, "absent de la sauvegarde")); continue; }
            var selected = choices.Where(c => c.Selected).ToList();
            if (selected.Count == 0) { apps.Add(new SummaryLine(MarkKind.Off, name, "non inclus")); continue; }
            if (selected.Any(c => c.Target is null)) { apps.Add(new SummaryLine(MarkKind.Warn, name, "profil de destination à choisir")); continue; }
            var running = BrowserProcessService.IsRunning(kind);
            var detail = string.Join(", ", selected.Select(c => $"{c.Profile.DisplayName} → {c.Target!.Label.Replace("Profil actuel : ", "")}"));
            apps.Add(new SummaryLine(running ? MarkKind.Warn : MarkKind.Ok, name, running ? detail + " · ouvert" : detail));
        }
        groups.Add(new SummaryGroup("Applications", apps));

        var wifiCount = s.Backup.Wifi.Count(w => s.SelectedWifi.Contains(w.RelativePath));
        groups.Add(new SummaryGroup("Réseau",
        [
            s.Backup.Wifi.Count == 0 ? new SummaryLine(MarkKind.Off, "Profils Wi-Fi", "absent de la sauvegarde")
            : wifiCount > 0 ? new SummaryLine(MarkKind.Ok, Format.Plural(wifiCount, "profil Wi-Fi", "profils Wi-Fi"),
                s.WifiExisting == WifiExistingPolicy.Replace ? "profils existants remplacés" : "profils existants conservés")
            : new SummaryLine(MarkKind.Off, "Profils Wi-Fi", "aucun sélectionné"),
        ]));
        groups.Add(new SummaryGroup("Windows",
        [
            s.Backup.Wallpaper is null ? new SummaryLine(MarkKind.Off, "Fond d'écran", "absent de la sauvegarde")
            : s.IncludeWallpaper ? new SummaryLine(MarkKind.Ok, "Fond d'écran", Format.Size(s.Backup.Wallpaper.Size))
            : new SummaryLine(MarkKind.Off, "Fond d'écran", "non inclus"),
        ]));
        Groups = groups;

        Mappings = plan.Items.Select(i => new MappingLine(
            i.Category == RestoreCategories.Personal ? i.Label : $"{i.Label}",
            $@"{Path.GetFileName(plan.Backup.Root)}\{i.RelativeSourcePath}",
            i.DestinationPath,
            i.DestinationNote ?? "",
            i.DestinationIsOneDrive)).ToList();

        Volumes = analysis?.Volumes.Select(v => new VolumeLine(v.Root.TrimEnd('\\'), Format.Size(v.NeededBytes) + " nécessaires",
            v.FreeBytes is { } f ? Format.Size(f) + " disponibles" : "espace inconnu", v.Sufficient)).ToList() ?? [];

        var validation = RestorePlanner.Validate(s, plan, analysis);
        Issues = validation.Where(i => i.IsBlocking).Select(i => i.Message).ToList();
        var warnings = validation.Where(i => !i.IsBlocking).Select(i => i.Message).ToList();
        foreach (var card in Restore.Browsers.Cards.Where(c => c.IsRunning && c.AnySelected))
            warnings.Add($"{card.Name} est ouvert : il sera fermé complètement au lancement de la restauration, après votre confirmation.");
        if (analysis is { Skipped: > 0 } an)
            warnings.Add($"{Format.Plural(an.Skipped, "élément de la sauvegarde ne sera pas restauré", "éléments de la sauvegarde ne seront pas restaurés")} (liens ou noms refusés par sécurité).");
        if (analysis is { Errors: > 0 } ae)
            warnings.Add($"{Format.Plural(ae.Errors, "conflit de type", "conflits de type")} (fichier ↔ dossier du même nom) : détaillés dans le journal.");
        Warnings = warnings;

        foreach (var name in new[]
                 {
                     nameof(SourceComputer), nameof(SourceUser), nameof(SourceDate), nameof(SourceVersion), nameof(Generation), nameof(IsLegacy), nameof(BackupPath),
                     nameof(TargetComputer), nameof(TargetUser), nameof(TargetProfile), nameof(HasIssues), nameof(HasWarnings),
                     nameof(IsAnalyzing), nameof(AnalysisText), nameof(FilesToRestoreText), nameof(VolumeText), nameof(SelectedText), nameof(NewText),
                     nameof(ExistingText), nameof(ReplacedText), nameof(KeptText), nameof(IdenticalText), nameof(WifiText),
                     nameof(PolicyReplace), nameof(PolicyKeep), nameof(PolicyNewer), nameof(PolicyText), nameof(CanStart), nameof(BlockReason),
                 })
            OnPropertyChanged(name);

        Status = HasIssues ? $"{Issues.Count} point(s) à corriger" : analysis is null ? "Analyse…" : $"{Format.Size(analysis.WriteBytes)} à restaurer";
        IsDone = CanStart;
        CommandManager.InvalidateRequerySuggested();
    }
}

// ====================================================================== 9. Restauration

public sealed class RestoreProgressViewModel : RestorePageViewModel
{
    private RestoreProgress? _progress;
    private CancellationTokenSource? _cts;
    private Task<RestoreResult>? _task;
    private readonly Stopwatch _watch = new();
    private RestoreResult? _result;
    private bool _isRunning;
    private bool _isCancelling;
    private long _lastBytes;
    private double _speed;
    private TimeSpan _lastSpeedSample;

    public RestoreProgressViewModel(RestoreViewModel restore) : base(restore, 9, "Restauration", "", "R_Nav_Progress")
    {
        CancelCommand = new RelayCommand(RequestCancel, () => IsRunning && !IsCancelling);
        OpenLogCommand = new RelayCommand(() => Open(_result?.LogPath), () => _result?.LogPath != null);
        OpenProfileCommand = new RelayCommand(() => Open(Restore.Profile.ProfilePath));
        FinishCommand = new RelayCommand(() => Restore.Finish(), () => IsFinished);
        Status = "En attente";
    }

    public ICommand CancelCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenProfileCommand { get; }
    public ICommand FinishCommand { get; }

    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(IsFinished)); } }
    public bool IsFinished => _result != null;
    public bool IsCancelling { get => _isCancelling; private set => Set(ref _isCancelling, value); }
    public Task<RestoreResult>? RunningTask => _task;

    // live
    public string BackupPath { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string CurrentFile { get; private set; } = "";
    public string FilesText { get; private set; } = "";
    public string BytesText { get; private set; } = "";
    public string DetailText { get; private set; } = "";
    public double Percent { get; private set; }
    public string PercentText => $"{Percent:0} %";
    public bool IsCounting { get; private set; }
    public int Errors { get; private set; }
    public long Replaced { get; private set; }
    public string KeptText { get; private set; } = "0";
    public int Ignored { get; private set; }
    public string ElapsedText { get; private set; } = "00:00";
    public string SpeedText { get; private set; } = "";
    public string RemainingText { get; private set; } = "";
    public ObservableCollection<CopyIssue> RecentIssues { get; } = [];

    // result
    public string ResultTitle { get; private set; } = "";
    public string ResultSubtitle { get; private set; } = "";
    public MarkKind ResultKind { get; private set; }
    public bool ResultWarn => ResultKind == MarkKind.Warn;
    public bool ResultFailed => ResultKind == MarkKind.Off;
    public IReadOnlyList<ResultLine> ResultLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> BrowserLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> WifiLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> WindowsLines { get; private set; } = [];
    public string KpiFiles { get; private set; } = "";
    public string KpiBytes { get; private set; } = "";
    public string KpiReplaced { get; private set; } = "";
    public string KpiKept { get; private set; } = "";
    public string KpiErrors { get; private set; } = "";
    public bool HasErrors { get; private set; }
    public string DurationText { get; private set; } = "";
    public string ControlText { get; private set; } = "";
    public string SafetyText { get; private set; } = "";
    public string? LogPath => _result?.LogPath;

    public void Start(RestorePlan plan)
    {
        _progress = new RestoreProgress();
        _cts = new CancellationTokenSource();
        BackupPath = plan.Backup.Root;
        OnPropertyChanged(nameof(BackupPath));
        IsRunning = true;
        Status = "Restauration…";
        _watch.Restart();
        _lastBytes = 0;
        _lastSpeedSample = TimeSpan.Zero;
        _task = new RestoreEngine(plan, _progress).RunAsync(_cts.Token);
        _task.ContinueWith(Finish, TaskScheduler.FromCurrentSynchronizationContext());
        Update();
    }

    public void RequestCancel()
    {
        if (!IsRunning || IsCancelling) return;
        if (!Views.DataBridgeDialog.ConfirmCancelRestore(Application.Current.MainWindow)) return;
        CancelWithoutPrompt();
    }

    public void CancelWithoutPrompt()
    {
        if (!IsRunning) return;
        IsCancelling = true;
        _cts?.Cancel();
        Status = "Annulation…";
    }

    public override void OnTick() => Update();

    private void Update()
    {
        var p = _progress;
        if (p is null || !IsRunning) return;
        var elapsed = _watch.Elapsed;
        ElapsedText = Format.Duration(elapsed);
        IsCounting = p.Counting;
        if (p.Counting)
        {
            Category = "Analyse des fichiers existants…";
            CurrentFile = $"{Format.Count(Interlocked.Read(ref p.CountedFiles))} fichiers comparés";
            FilesText = "Préparation de la restauration…";
            BytesText = "";
            DetailText = "";
            Percent = 0;
        }
        else
        {
            var processedBytes = p.ProcessedBytesWithCurrent;
            Category = string.IsNullOrEmpty(p.Item) ? p.Category : p.Category == p.Item ? p.Item : $"{p.Category} — {p.Item}";
            CurrentFile = IsCancelling ? "Arrêt après le fichier en cours… " + p.CurrentFile : p.CurrentFile;
            FilesText = $"{Format.Count(p.WrittenFiles)} / {Format.Count(p.PlannedFiles)} fichiers restaurés";
            BytesText = $"{Format.Size(p.WrittenBytesWithCurrent)} / {Format.Size(p.PlannedBytes)}";
            DetailText = p.WifiTotal > 0 ? $"Profils Wi-Fi : {p.WifiDone} / {p.WifiTotal}" : "";
            Percent = p.PlannedBytes > 0 ? Math.Min(100, processedBytes * 100.0 / p.PlannedBytes)
                : p.PlannedFiles > 0 ? Math.Min(100, p.ProcessedFiles * 100.0 / p.PlannedFiles) : 100;
            if (elapsed - _lastSpeedSample >= TimeSpan.FromSeconds(1))
            {
                var instant = (processedBytes - _lastBytes) / Math.Max(0.001, (elapsed - _lastSpeedSample).TotalSeconds);
                _speed = _speed <= 0 ? instant : _speed * 0.7 + instant * 0.3;
                _lastBytes = processedBytes;
                _lastSpeedSample = elapsed;
                SpeedText = _speed > 0 ? $"{Format.Size((long)_speed)}/s" : "";
                var remaining = p.PlannedBytes - processedBytes;
                RemainingText = _speed > 1024 && remaining > 0 ? "≈ " + Format.Duration(TimeSpan.FromSeconds(remaining / _speed)) + " restantes" : "";
            }
        }
        Errors = p.Errors;
        Replaced = Interlocked.Read(ref p.ReplacedFiles);
        KeptText = p.IdenticalFiles > 0 ? $"{Format.Count(p.KeptFiles)} (+{Format.Count(p.IdenticalFiles)} identiques)" : Format.Count(p.KeptFiles);
        Ignored = p.Skipped + p.InterruptedFiles;

        var recent = p.RecentIssues();
        if (recent.Count != RecentIssues.Count || (recent.Count > 0 && RecentIssues.Count > 0 && recent[0] != RecentIssues[0]))
        {
            RecentIssues.Clear();
            foreach (var issue in recent.Take(60)) RecentIssues.Add(issue);
        }
        Status = p.Counting ? "Analyse…" : $"{Percent:0} % · {Format.Size(p.WrittenBytesWithCurrent)}";
        foreach (var name in new[]
                 {
                     nameof(Category), nameof(CurrentFile), nameof(FilesText), nameof(BytesText), nameof(DetailText), nameof(Percent), nameof(PercentText),
                     nameof(IsCounting), nameof(Errors), nameof(Replaced), nameof(KeptText), nameof(Ignored), nameof(ElapsedText), nameof(SpeedText), nameof(RemainingText),
                 })
            OnPropertyChanged(name);
    }

    private void Finish(Task<RestoreResult> task)
    {
        Update();
        _watch.Stop();
        IsRunning = false;
        IsCancelling = false;
        var r = task.IsCompletedSuccessfully ? task.Result : new RestoreResult { Outcome = RestoreOutcome.Failed, FatalMessage = task.Exception?.InnerException?.Message ?? "Erreur inconnue" };
        _result = r;

        (ResultKind, ResultTitle) = r.Outcome switch
        {
            RestoreOutcome.Completed when !r.HasWarnings => (MarkKind.Ok, "Restauration terminée"),
            RestoreOutcome.Completed => (MarkKind.Warn, "Restauration terminée avec des avertissements"),
            RestoreOutcome.Cancelled => (MarkKind.Warn, "Restauration interrompue par l'utilisateur."),
            _ => (MarkKind.Off, "Restauration interrompue : " + (r.FatalMessage ?? "erreur critique")),
        };
        ResultSubtitle = r.Outcome == RestoreOutcome.Cancelled
            ? "Les éléments déjà restaurés ont été conservés. Le journal décrit ce qui a été restauré."
            : $"Sauvegarde : {BackupPath}";

        var lines = new List<ResultLine>
        {
            new(MarkKind.Ok, Format.Plural(r.WrittenFiles, "fichier restauré", "fichiers restaurés") + $" · {Format.Size(r.WrittenBytes)}"),
            new(r.ReplacedFiles > 0 ? MarkKind.Ok : MarkKind.Off, "dont " + Format.Plural(r.ReplacedFiles, "fichier existant remplacé", "fichiers existants remplacés")),
            new(r.KeptFiles > 0 ? MarkKind.Ok : MarkKind.Off, Format.Plural(r.KeptFiles, "fichier existant conservé", "fichiers existants conservés")),
        };
        if (r.IdenticalFiles > 0) lines.Add(new(MarkKind.Ok, Format.Plural(r.IdenticalFiles, "fichier déjà identique", "fichiers déjà identiques")));
        lines.Add(r.ErrorCount == 0 ? new(MarkKind.Off, "0 erreur") : new(MarkKind.Error, Format.Plural(r.ErrorCount, "erreur", "erreurs") + " (détail dans le journal)"));
        if (r.SkippedCount > 0) lines.Add(new(MarkKind.Warn, Format.Plural(r.SkippedCount, "élément ignoré", "éléments ignorés") + " (liens, noms refusés)"));
        if (r.InterruptedFiles > 0) lines.Add(new(MarkKind.Warn, Format.Plural(r.InterruptedFiles, "fichier interrompu", "fichiers interrompus") + " par l'annulation"));
        if (r.NotProcessedFiles > 0) lines.Add(new(MarkKind.Warn, Format.Plural(r.NotProcessedFiles, "fichier non traité", "fichiers non traités") + " (restauration arrêtée)"));
        ResultLines = lines;

        var browsers = r.Items.Where(i => i.Item.Browser != null).Select(i =>
            i.Errors == 0 && i.Started
                ? new ResultLine(MarkKind.Ok, $"{BrowserProcessService.DisplayName(i.Item.Browser!.Kind).Split(' ')[^1]} restauré ({i.Item.Browser.SourceDisplayName} → {i.Item.Browser.TargetLabel.Replace("Profil actuel : ", "")})")
                : new ResultLine(MarkKind.Error, $"{BrowserProcessService.DisplayName(i.Item.Browser!.Kind).Split(' ')[^1]} : {Format.Plural(i.Errors, "erreur", "erreurs")}")).ToList();
        if (browsers.Count == 0) browsers.Add(new(MarkKind.Off, "Aucun navigateur restauré"));
        else browsers.Add(new(MarkKind.Warn, "Mots de passe, cookies et sessions non garantis"));
        BrowserLines = browsers;

        var wifi = new List<ResultLine>();
        if (r.Wifi.Count > 0)
        {
            var restored = r.Wifi.Count(w => w.Restored);
            if (restored > 0) wifi.Add(new(MarkKind.Ok, Format.Plural(restored, "profil restauré", "profils restaurés")));
            var kept = r.Wifi.Count(w => w.Outcome == WifiRestoreOutcome.KeptExisting);
            if (kept > 0) wifi.Add(new(MarkKind.Ok, Format.Plural(kept, "profil déjà présent conservé", "profils déjà présents conservés")));
            var notRestored = r.Wifi.Count(w => w.Outcome is WifiRestoreOutcome.NotRestored or WifiRestoreOutcome.Failed);
            if (notRestored > 0) wifi.Add(new(MarkKind.Warn, Format.Plural(notRestored, "profil non restauré avec les droits de la session", "profils non restaurés avec les droits de la session")));
        }
        else wifi.Add(new(MarkKind.Off, "Aucun profil Wi-Fi restauré"));
        WifiLines = wifi;

        WindowsLines = r.Wallpaper is { } wp
            ? [new(wp.Applied || (wp.Copied && !wp.ApplyRequested) ? MarkKind.Ok : wp.Copied ? MarkKind.Warn : MarkKind.Error, wp.Applied ? "Fond d'écran appliqué" : wp.Message)]
            : [new(MarkKind.Off, "Fond d'écran non restauré")];

        KpiFiles = Format.Count(r.WrittenFiles);
        KpiBytes = Format.Size(r.WrittenBytes);
        KpiReplaced = Format.Count(r.ReplacedFiles);
        KpiKept = Format.Count(r.KeptFiles);
        KpiErrors = Format.Count(r.ErrorCount);
        HasErrors = r.ErrorCount > 0;
        DurationText = Format.Duration(r.Duration);
        ControlText = $"Sélection : {Format.Count(r.SelectedFiles)} fichiers  ·  à écrire : {Format.Count(r.PlannedFiles)}  ·  écrits : {Format.Count(r.WrittenFiles)}"
                      + $"  ·  en erreur : {Format.Count(r.ErrorFiles)}" + (r.IdenticalFiles > 0 ? $"  ·  identiques : {Format.Count(r.IdenticalFiles)}" : "");
        SafetyText = r.SafetyUsed ? $"Copies de sécurité des fichiers remplacés : {r.SafetyRoot}" : "";
        Status = r.Outcome switch { RestoreOutcome.Completed => "Terminée", RestoreOutcome.Cancelled => "Interrompue", _ => "Erreur" };
        IsDone = r.Outcome == RestoreOutcome.Completed;

        foreach (var name in new[]
                 {
                     nameof(IsFinished), nameof(ResultTitle), nameof(ResultSubtitle), nameof(ResultKind), nameof(ResultWarn), nameof(ResultFailed),
                     nameof(ResultLines), nameof(BrowserLines), nameof(WifiLines), nameof(WindowsLines), nameof(KpiFiles), nameof(KpiBytes),
                     nameof(KpiReplaced), nameof(KpiKept), nameof(KpiErrors), nameof(HasErrors), nameof(DurationText), nameof(ControlText),
                     nameof(SafetyText), nameof(LogPath),
                 })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
        Restore.RestoreFinished();
    }

    private static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainViewModel.ShowError("Ouverture impossible : " + ex.Message);
        }
    }
}
