using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;
using DataBridge.Core.Logging;

namespace DataBridge.App.ViewModels;

public sealed record ResultLine(MarkKind Kind, string Text)
{
    public bool IsOk => Kind == MarkKind.Ok;
    public bool IsWarn => Kind is MarkKind.Warn or MarkKind.Partial;
    public bool IsOff => Kind == MarkKind.Off;
    public bool IsCloud => Kind == MarkKind.Cloud;
    public bool IsError => Kind == MarkKind.Error;
}

/// <summary>Copy in progress (live counters) then final result.</summary>
public sealed class CopyViewModel : PageViewModel
{
    private CopyProgress? _progress;
    private CancellationTokenSource? _cts;
    private Task<CopyResult>? _task;
    private readonly Stopwatch _watch = new();
    private CopyResult? _result;
    private bool _isRunning;
    private bool _isCancelling;
    private long _lastBytes;
    private double _speed;
    private TimeSpan _lastSpeedSample;

    public CopyViewModel(MainViewModel main) : base(main, 11, "Copie", "", "Nav_Copy")
    {
        CancelCommand = new RelayCommand(RequestCancel, () => IsRunning && !IsCancelling);
        OpenBackupCommand = new RelayCommand(() => Open(_result?.BackupPath), () => _result != null);
        OpenLogCommand = new RelayCommand(() => Open(_result?.LogPath), () => _result?.LogPath != null);
        FinishCommand = new RelayCommand(() => Main.Finish());
        Status = "En attente";
    }

    public ICommand CancelCommand { get; }
    public ICommand OpenBackupCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand FinishCommand { get; }

    public bool IsRunning { get => _isRunning; private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(IsFinished)); } }
    public bool IsFinished => _result != null;
    public bool IsCancelling { get => _isCancelling; private set => Set(ref _isCancelling, value); }
    public Task<CopyResult>? RunningTask => _task;

    // ---------------------------------------------------------------- live
    public string BackupPath { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string CurrentFile { get; private set; } = "";
    public string FilesText { get; private set; } = "";
    public string BytesText { get; private set; } = "";
    public string SelectionText { get; private set; } = "";
    public double Percent { get; private set; }
    public string PercentText => $"{Percent:0} %";
    public bool IsCounting { get; private set; }
    public int Errors { get; private set; }
    public string ErrorsDetail { get; private set; } = "";
    public int Ignored { get; private set; }
    public string CloudSkippedText { get; private set; } = "0";
    public int Duplicates { get; private set; }
    public string ElapsedText { get; private set; } = "00:00";
    public string SpeedText { get; private set; } = "";
    public string RemainingText { get; private set; } = "";
    public ObservableCollection<CopyIssue> RecentIssues { get; } = [];

    // ---------------------------------------------------------------- result
    public string ResultTitle { get; private set; } = "";
    public string ResultSubtitle { get; private set; } = "";
    public MarkKind ResultKind { get; private set; }
    public bool ResultOk => ResultKind == MarkKind.Ok;
    public bool ResultWarn => ResultKind == MarkKind.Warn;
    public bool ResultFailed => ResultKind == MarkKind.Off;
    public IReadOnlyList<ResultLine> ResultLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> WifiLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> NetworkDriveLines { get; private set; } = [];
    public IReadOnlyList<ResultLine> ExtraLines { get; private set; } = [];
    public double ProcessedPercent { get; private set; }
    // Key figures of the result screen (same CopyResult as the log).
    public string KpiFiles { get; private set; } = "";
    public string KpiBytes { get; private set; } = "";
    public string KpiErrors { get; private set; } = "";
    public bool HasErrors { get; private set; }
    public string ProcessedText { get; private set; } = "";
    public string ControlText { get; private set; } = "";
    public string DurationText { get; private set; } = "";
    public string? LogPath => _result?.LogPath;

    /// <summary>Starts the copy. The engine first counts what is really planned (progress denominators).</summary>
    public void Start(CopyPlan plan)
    {
        _progress = new CopyProgress();
        _cts = new CancellationTokenSource();
        BackupPath = plan.BackupRoot;
        OnPropertyChanged(nameof(BackupPath));
        IsRunning = true;
        Status = "Copie en cours…";
        _watch.Restart();
        _lastBytes = 0;
        _lastSpeedSample = TimeSpan.Zero;
        _task = new CopyEngine(plan, _progress).RunAsync(_cts.Token);
        _task.ContinueWith(t => Finish(t), TaskScheduler.FromCurrentSynchronizationContext());
        Update();
    }

    /// <summary>Asks for confirmation, then stops after the current file (already copied data is kept).</summary>
    public void RequestCancel()
    {
        if (!IsRunning || IsCancelling) return;
        if (!Views.DataBridgeDialog.ConfirmCancelCopy(Application.Current.MainWindow)) return;
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
            // Exact count of the planned files before the first copy (a few seconds at most).
            Category = "Analyse de la sélection…";
            CurrentFile = $"{Format.Count(Interlocked.Read(ref p.CountedFiles))} fichiers recensés";
            FilesText = "Comptage des fichiers à copier…";
            BytesText = "";
            SelectionText = "";
            Percent = 0;
        }
        else if (p.ExtraPhase)
        {
            // Printers / Outlook mailbox, after the files: progress in items of the engine, not in bytes.
            Category = string.IsNullOrEmpty(p.Item) ? p.Category : $"{p.Category} — {p.Item}";
            CurrentFile = IsCancelling ? "Arrêt en cours… " + p.CurrentFile : p.CurrentFile;
            FilesText = p.ExtraTotal > 0 ? $"{Format.Count(p.ExtraDone)} / {Format.Count(p.ExtraTotal)} éléments" : "En cours…";
            BytesText = $"Fichiers : {Format.Count(Interlocked.Read(ref p.CopiedFiles))} copiés · {Format.Size(p.CopiedBytesWithCurrent)}";
            SelectionText = "";
            Percent = p.ExtraTotal > 0 ? Math.Min(100, p.ExtraDone * 100.0 / p.ExtraTotal) : 100;
            SpeedText = "";
            RemainingText = "";
        }
        else
        {
            var copiedBytes = p.CopiedBytesWithCurrent;
            var processedBytes = p.ProcessedBytesWithCurrent;
            var copied = Interlocked.Read(ref p.CopiedFiles);
            Category = string.IsNullOrEmpty(p.Item) ? p.Category : p.Category == p.Item ? p.Item : $"{p.Category} — {p.Item}";
            CurrentFile = IsCancelling ? "Arrêt après le fichier en cours… " + p.CurrentFile : p.CurrentFile;
            // Denominators = files really planned: cloud-only files left online are never part of them.
            FilesText = $"{Format.Count(copied)} / {Format.Count(p.PlannedFiles)} fichiers copiés";
            BytesText = $"{Format.Size(copiedBytes)} / {Format.Size(p.PlannedBytes)}";
            SelectionText = p.PlannedCloudSkipped > 0
                ? $"Sélection : {Format.Count(p.SelectedFiles)} fichiers  ·  à copier : {Format.Count(p.PlannedFiles)}  ·  {Format.Count(p.PlannedCloudSkipped)} en ligne non téléchargés"
                : $"Sélection : {Format.Count(p.SelectedFiles)} fichiers  ·  à copier : {Format.Count(p.PlannedFiles)}";
            Percent = p.PlannedBytes > 0 ? Math.Min(100, processedBytes * 100.0 / p.PlannedBytes)
                : p.PlannedFiles > 0 ? Math.Min(100, p.ProcessedFiles * 100.0 / p.PlannedFiles) : 0;

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
        ErrorsDetail = p.Locked > 0 ? $"dont {Format.Plural(p.Locked, "verrouillé", "verrouillés")}" : "";
        Ignored = p.SkippedFiles + p.InterruptedFiles;
        CloudSkippedText = p.PlannedCloudSkipped > 0 ? $"{Format.Count(p.CloudSkipped)} / {Format.Count(p.PlannedCloudSkipped)}" : Format.Count(p.CloudSkipped);
        Duplicates = p.Duplicates;

        var recent = p.RecentIssues();
        if (recent.Count != RecentIssues.Count || (recent.Count > 0 && RecentIssues.Count > 0 && recent[0] != RecentIssues[0]))
        {
            RecentIssues.Clear();
            foreach (var issue in recent.Take(60)) RecentIssues.Add(issue);
        }

        Status = p.Counting ? "Analyse…" : $"{Percent:0} % · {Format.Size(p.CopiedBytesWithCurrent)}";
        foreach (var name in new[]
                 {
                     nameof(Category), nameof(CurrentFile), nameof(FilesText), nameof(BytesText), nameof(SelectionText), nameof(Percent), nameof(PercentText),
                     nameof(IsCounting), nameof(Errors), nameof(ErrorsDetail), nameof(Ignored), nameof(CloudSkippedText), nameof(Duplicates),
                     nameof(ElapsedText), nameof(SpeedText), nameof(RemainingText),
                 })
            OnPropertyChanged(name);
    }

    private void Finish(Task<CopyResult> task)
    {
        Update();
        _watch.Stop();
        IsRunning = false;
        IsCancelling = false;

        var r = task.IsCompletedSuccessfully ? task.Result : new CopyResult
        {
            Outcome = CopyOutcome.Failed,
            FatalMessage = task.Exception?.InnerException?.Message ?? "Erreur inconnue",
            BackupPath = BackupPath,
        };
        _result = r;

        (ResultKind, ResultTitle) = r.Outcome switch
        {
            CopyOutcome.Completed when r.ErrorCount == 0 && r.ExtrasOk => (MarkKind.Ok, "Sauvegarde terminée"),
            CopyOutcome.Completed => (MarkKind.Warn, "Sauvegarde terminée avec des avertissements"),
            CopyOutcome.Cancelled => (MarkKind.Warn, "Copie interrompue par l'utilisateur."),
            _ => (MarkKind.Off, "Copie interrompue : " + (r.FatalMessage ?? "erreur critique")),
        };
        ResultSubtitle = r.Outcome == CopyOutcome.Cancelled
            ? "Les éléments déjà copiés ont été conservés. Le journal décrit ce qui a été copié."
            : r.BackupPath;

        // Same counters as copy-log.txt (both read this CopyResult). Cloud-only files left online are never "copied".
        var lines = new List<ResultLine>
        {
            new(MarkKind.Ok, Format.Plural(r.CopiedFileCount, "fichier copié", "fichiers copiés")),
            new(MarkKind.Ok, Format.Size(r.CopiedBytes) + " copiés"),
        };
        if (r.SkippedFileCount > 0)
            lines.Add(new(MarkKind.Warn, Format.Plural(r.SkippedFileCount, "élément ignoré", "éléments ignorés") + DescribeIgnored(r)));
        if (r.SkippedCloudOnlyCount > 0)
            lines.Add(new(MarkKind.Cloud, Format.Plural(r.SkippedCloudOnlyCount, "fichier cloud non téléchargé", "fichiers cloud non téléchargés")
                                          + $" ({Format.Size(r.SkippedCloudOnlyBytes)})"));
        lines.Add(r.ErrorCount == 0
            ? new(MarkKind.Off, "0 erreur")
            : new(MarkKind.Error, Format.Plural(r.ErrorCount, "erreur", "erreurs") + DescribeErrors(r)));
        if (r.InterruptedFileCount > 0)
            lines.Add(new(MarkKind.Warn, Format.Plural(r.InterruptedFileCount, "fichier interrompu", "fichiers interrompus") + " par l'annulation"));
        if (r.NotProcessedFileCount > 0)
            lines.Add(new(MarkKind.Warn, Format.Plural(r.NotProcessedFileCount, "fichier non traité", "fichiers non traités") + " (copie arrêtée)"));
        if (r.CopiedWhileOpenCount > 0)
            lines.Add(new(MarkKind.Partial, Format.Plural(r.CopiedWhileOpenCount, "fichier ouvert copié en lecture partagée", "fichiers ouverts copiés en lecture partagée")));
        if (r.DuplicateCount > 0)
            lines.Add(new(MarkKind.Ok, Format.Plural(r.DuplicateCount, "doublon évité", "doublons évités")));
        ResultLines = lines;

        KpiFiles = Format.Count(r.CopiedFileCount);
        KpiBytes = Format.Size(r.CopiedBytes);
        KpiErrors = Format.Count(r.ErrorCount);
        HasErrors = r.ErrorCount > 0;
        ProcessedPercent = r.ProcessedPercent;
        ProcessedText = $"{ProcessedPercent:0} %  ·  {Format.Count(r.ProcessedFileCount)} / {Format.Count(r.PlannedCopyFileCount)} fichiers prévus traités";
        ControlText = $"Sélection : {Format.Count(r.SelectedFileCount)} fichiers  ·  prévus pour la copie : {Format.Count(r.PlannedCopyFileCount)}"
                      + (r.SkippedCloudOnlyCount > 0 ? $"  ·  {Format.Count(r.SkippedCloudOnlyCount)} en ligne non téléchargés" : "")
                      + "\nContrôle : " + CopyLogWriter.ControlText(r);

        var wifi = new List<ResultLine>();
        if (r.Wifi.Count > 0)
        {
            wifi.Add(new(r.WifiExported == r.Wifi.Count ? MarkKind.Ok : MarkKind.Warn, Format.Plural(r.WifiExported, "profil sauvegardé", "profils sauvegardés")));
            var failed = r.Wifi.Count - r.WifiExported;
            if (failed > 0) wifi.Add(new(MarkKind.Error, Format.Plural(failed, "profil non exporté", "profils non exportés")));
            if (r.WifiKeysProtected > 0) wifi.Add(new(MarkKind.Warn, Format.Plural(r.WifiKeysProtected, "clé protégée par Windows", "clés protégées par Windows")));
            if (r.WifiKeysIncluded > 0) wifi.Add(new(MarkKind.Ok, Format.Plural(r.WifiKeysIncluded, "clé incluse", "clés incluses") + " dans l'export" +
                (r.WifiKeysViaAdmin > 0 ? $" (dont {r.WifiKeysViaAdmin} avec les droits administrateur)" : "")));
            if (r.WifiWithoutKey > 0) wifi.Add(new(MarkKind.Ok, Format.Plural(r.WifiWithoutKey, "réseau sans clé partagée", "réseaux sans clé partagée")));
        }
        else
        {
            wifi.Add(new(MarkKind.Off, "Aucun profil Wi-Fi exporté"));
        }
        WifiLines = wifi;
        NetworkDriveLines = r.NetworkDrives.Count > 0
            ? [new(MarkKind.Ok, Format.Plural(r.NetworkDrives.Count, "lecteur réseau enregistré", "lecteurs réseau enregistrés")
                                + " : " + string.Join(", ", r.NetworkDrives.Select(d => d.Letter))),
               new(MarkKind.Warn, "Mots de passe non sauvegardés (demandés à la restauration)")]
            : [new(MarkKind.Off, "Aucun lecteur réseau enregistré")];
        ExtraLines = [.. ExtraResultLines.Outlook(r.Outlook, restore: false), .. ExtraResultLines.PrintersBackup(r.Printers)];
        DurationText = Format.Duration(r.Duration);
        Status = r.Outcome switch { CopyOutcome.Completed => "Terminée", CopyOutcome.Cancelled => "Interrompue", _ => "Erreur" };
        IsDone = r.Outcome == CopyOutcome.Completed;

        foreach (var name in new[]
                 {
                     nameof(IsFinished), nameof(ResultTitle), nameof(ResultSubtitle), nameof(ResultKind), nameof(ResultOk), nameof(ResultWarn),
                     nameof(ResultFailed), nameof(ResultLines), nameof(WifiLines), nameof(NetworkDriveLines), nameof(ExtraLines), nameof(DurationText), nameof(LogPath),
                     nameof(ProcessedPercent), nameof(ProcessedText), nameof(ControlText),
                     nameof(KpiFiles), nameof(KpiBytes), nameof(KpiErrors), nameof(HasErrors),
                 })
            OnPropertyChanged(name);
        CommandManager.InvalidateRequerySuggested();
        Main.CopyFinished();
    }

    private static string DescribeIgnored(CopyResult r) =>
        r.Count(IssueKind.Link) > 0 ? $" ({Format.Plural(r.Count(IssueKind.Link), "lien/jonction non suivi", "liens/jonctions non suivis")})" : "";

    private static string DescribeErrors(CopyResult r)
    {
        var parts = new List<string>();
        if (r.LockedFileCount > 0) parts.Add(Format.Plural(r.LockedFileCount, "fichier verrouillé", "fichiers verrouillés"));
        var otherFiles = r.ErrorFileCount - r.LockedFileCount;
        if (otherFiles > 0) parts.Add(Format.Plural(otherFiles, r.LockedFileCount > 0 ? "autre fichier" : "fichier", r.LockedFileCount > 0 ? "autres fichiers" : "fichiers"));
        if (r.ErrorFolderCount > 0) parts.Add(Format.Plural(r.ErrorFolderCount, "dossier", "dossiers"));
        return " : " + string.Join(", ", parts);
    }

    private static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MainViewModel.ShowError("Ouverture impossible : " + ex.Message);
        }
    }
}
