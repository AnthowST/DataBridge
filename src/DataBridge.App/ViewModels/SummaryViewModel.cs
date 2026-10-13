using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

public enum MarkKind { Ok, Partial, Off, Warn, Cloud, Error }

public sealed record SummaryLine(MarkKind Kind, string Text, string Detail)
{
    public string Mark => Kind switch { MarkKind.Ok => "", MarkKind.Partial => "", MarkKind.Warn => "", _ => "" };
    public bool IsOk => Kind == MarkKind.Ok;
    public bool IsPartial => Kind == MarkKind.Partial;
    public bool IsOff => Kind == MarkKind.Off;
    public bool IsWarn => Kind == MarkKind.Warn;
}

public sealed record SummaryGroup(string Title, IReadOnlyList<SummaryLine> Lines);

public sealed class SummaryViewModel : PageViewModel
{
    private const long SafetyMargin = 64L * 1024 * 1024;
    private IReadOnlyList<SummaryGroup> _groups = [];
    private IReadOnlyList<string> _warnings = [];

    public SummaryViewModel(MainViewModel main) : base(main, 10, "Résumé", "", "Nav_Summary")
    {
        StartCommand = new RelayCommand(() => Main.StartCopy(), () => CanStart);
        BackToSelectionCommand = new RelayCommand(() => Main.GoTo(Main.Hub));
        ChooseDestinationCommand = new RelayCommand(() => Main.GoTo(Main.Destination));
        Status = "Vérification avant copie";
    }

    public ICommand StartCommand { get; }
    public ICommand BackToSelectionCommand { get; }
    public ICommand ChooseDestinationCommand { get; }

    public string Machine => Main.Profile.MachineName;
    public string User => Main.Profile.UserName;
    public string DestinationText => Main.Destination.ChosenPath is null ? "Aucune destination choisie" : Main.Destination.FolderPreview;
    public IReadOnlyList<SummaryGroup> Groups { get => _groups; private set => Set(ref _groups, value); }
    public IReadOnlyList<string> Warnings { get => _warnings; private set => Set(ref _warnings, value); }
    public bool HasWarnings => _warnings.Count > 0;

    private Totals? Sum => Main.Totals?.Sum;
    private CopyForecast? Forecast => Main.Forecast;
    private bool SkipCloud => Main.Selection.SkipCloudOnly;
    public bool IsComplete => Main.TotalsComplete;
    /// <summary>Bytes really planned for the copy (cloud-only files left online excluded).</summary>
    public long PlannedBytes => Forecast?.PlannedCopyBytes ?? 0;
    public long? FreeBytes => Main.Destination.FreeBytes;

    public string PlannedBytesText => Forecast is null ? "Calcul…" : Format.Size(PlannedBytes) + (IsComplete ? "" : " (calcul en cours…)");
    public string PlannedFilesText => Forecast is not { } f ? ""
        : Format.Plural(f.PlannedCopyFileCount, "fichier", "fichiers") + (HasCloudFiles ? SkipCloud ? " locaux réellement à copier" : " (fichiers en ligne compris)" : "");
    public string FreeText => FreeBytes is { } f ? Format.Size(f) : "—";

    // Breakdown: selected = to copy + cloud-only files left online.
    public string SelectedFilesCell => Forecast is { } f ? Format.Count(f.SelectedFileCount) : "…";
    public string SelectedBytesCell => Forecast is { } f ? Format.Size(f.SelectedBytes) : "";
    public string PlannedFilesCell => Forecast is { } f ? Format.Count(f.PlannedCopyFileCount) : "…";
    public string PlannedBytesCell => Forecast is { } f ? Format.Size(f.PlannedCopyBytes) : "";
    public string CloudFilesCell => Sum is { } s ? Format.Count(s.CloudFiles) : "";
    public string CloudBytesCell => Sum is { } s ? Format.Size(s.CloudBytes) : "";
    public string CloudFateText => SkipCloud ? "Ne seront pas téléchargés" : "Seront téléchargés puis copiés";

    public bool HasCloudFiles => Sum is { CloudFiles: > 0 };
    public string CloudText => Sum is { } s && s.CloudFiles > 0
        ? $"Que faire des {Format.Plural(s.CloudFiles, "fichier sélectionné", "fichiers sélectionnés")} disponibles uniquement en ligne ({Format.Size(s.CloudBytes)}) ?"
        : "";

    public bool SkipCloudOnly
    {
        get => Main.Selection.SkipCloudOnly;
        set
        {
            Main.Selection.SkipCloudOnly = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DownloadCloudOnly));
            Main.NotifyCloudOptionChanged();
            Main.RequestRecalc();
            Refresh();
        }
    }

    public bool DownloadCloudOnly
    {
        get => !SkipCloudOnly;
        set => SkipCloudOnly = !value;
    }

    public bool HasDestination => Main.Destination.IsValid;
    public bool NothingSelected => Main.Totals is { } t && t.PerJob.Count == 0 && Main.Selection.SelectedWifi.Count == 0
                                   && Main.Selection.SelectedNetworkDrives.Count == 0
                                   && Main.Selection.SelectedPrinters.Count == 0 && Main.Selection.OutlookMailbox is null;
    public bool InsufficientSpace => IsComplete && FreeBytes is { } f && PlannedBytes + SafetyMargin > f;
    public string SpaceSelectedText => $"Données à copier : {Format.Size(PlannedBytes)}";
    public string SpaceFreeText => $"Espace disponible : {FreeText}";

    public bool HasConflict => Main.Conflict != null;
    public string ConflictDetail => Main.Conflict?.Detail ?? "";

    public bool CanStart => HasDestination && IsComplete && !InsufficientSpace && !NothingSelected && !HasConflict && !Main.IsCopying && !Main.IsLaunching;

    public string BlockReason =>
        Main.IsLaunching ? "Préparation du lancement (fermeture des navigateurs)…"
        : !HasDestination ? "Choisissez une destination valide avant de lancer la copie."
        : HasConflict ? "Destination invalide : le dossier de sauvegarde se trouve dans une source sélectionnée."
        : !IsComplete ? "Calcul de la taille de la sélection en cours…"
        : NothingSelected ? "Rien n'est sélectionné."
        : InsufficientSpace ? "Espace insuffisant sur la destination."
        : "";

    public override void OnActivated()
    {
        Main.Browsers.RefreshCommand.Execute(null);
        Refresh();
    }

    public override void OnTick()
    {
        OnPropertyChanged(nameof(DestinationText));
    }

    public void Refresh()
    {
        var s = Main.Selection;
        var totals = Main.Totals;
        var groups = new List<SummaryGroup>();

        Totals? JobTotals(Func<CopyJob, bool> match)
        {
            if (totals is null) return null;
            var jobs = totals.PerJob.Where(p => match(p.Key)).ToList();
            return jobs.Count == 0 ? null : jobs.Aggregate(Core.Copy.Totals.Zero, (a, p) => a + p.Value);
        }
        string SizeOf(Totals? t) => t is { } v ? (v.Complete ? Format.Size(v.Forecast(SkipCloud).PlannedCopyBytes) : "calcul…") : "";

        // Personal folders
        var personal = new List<SummaryLine>();
        foreach (var root in Main.Personal.Roots)
        {
            if (!root.Available) { personal.Add(new SummaryLine(MarkKind.Off, root.Label, "introuvable")); continue; }
            var t = JobTotals(j => j.Category == Categories.Personal && j.SourcePath == root.Model.Path);
            personal.Add(root.State switch
            {
                CheckState.Checked => new SummaryLine(MarkKind.Ok, root.Label, SizeOf(t)),
                CheckState.Indeterminate => new SummaryLine(MarkKind.Partial, root.Label, "sélection partielle · " + SizeOf(t)),
                _ => new SummaryLine(MarkKind.Off, root.Label, "non inclus"),
            });
        }
        groups.Add(new SummaryGroup("Dossiers personnels", personal));

        // Additional
        var statuses = s.AnalyzeAdditional(Main.BackupRootPreview);
        var additional = s.Additional.Select(a =>
        {
            var st = statuses[a];
            return st.Kind switch
            {
                AdditionalStatusKind.AlreadyIncluded => new SummaryLine(MarkKind.Off, a.Path, "déjà inclus (pas de doublon)"),
                AdditionalStatusKind.Missing => new SummaryLine(MarkKind.Warn, a.Path, "introuvable"),
                AdditionalStatusKind.ContainsDestination => new SummaryLine(MarkKind.Warn, a.Path, "contient la destination"),
                _ => new SummaryLine(st.Kind == AdditionalStatusKind.Ready ? MarkKind.Ok : MarkKind.Partial, a.Path,
                    SizeOf(JobTotals(j => j.Category == Categories.Additional && j.SourcePath == a.Path))),
            };
        }).ToList();
        if (additional.Count == 0) additional.Add(new SummaryLine(MarkKind.Off, "Aucun élément ajouté", ""));
        groups.Add(new SummaryGroup("Fichiers supplémentaires", additional));

        // Applications
        var apps = new List<SummaryLine>
        {
            s.Signatures is { Exists: true } && s.IncludeSignatures
                ? new SummaryLine(MarkKind.Ok, "Signatures Outlook", $"{s.Signatures.Names.Count} · {SizeOf(JobTotals(j => j.Category == Categories.Outlook))}")
                : new SummaryLine(MarkKind.Off, "Signatures Outlook", s.Signatures?.Exists == true ? "non incluses" : "aucune"),
            s.OutlookMailbox is { } mailbox
                ? new SummaryLine(Core.Outlook.OutlookSession.IsOutlookRunning() ? MarkKind.Ok : MarkKind.Warn, "Boîte Outlook",
                    $"{mailbox.StoreName} · {Core.Outlook.OutlookCategoryText.Describe(mailbox.Categories)}" + (Core.Outlook.OutlookSession.IsOutlookRunning() ? "" : " · Outlook fermé"))
                : new SummaryLine(MarkKind.Off, "Boîte Outlook", "non incluse"),
        };
        foreach (var card in Main.Browsers.Cards)
        {
            if (!card.Detected) { apps.Add(new SummaryLine(MarkKind.Off, card.ShortName, "non détecté")); continue; }
            if (!card.Model.Include) { apps.Add(new SummaryLine(MarkKind.Off, card.ShortName, "non inclus")); continue; }
            var detail = $"{card.Model.SelectedProfiles.Count} profil(s) · {SizeOf(JobTotals(j => j.Category == Categories.Browsers && j.Label.StartsWith(card.Name)))}";
            apps.Add(new SummaryLine(card.IsRunning ? MarkKind.Warn : MarkKind.Ok, card.ShortName, card.IsRunning ? detail + " · ouvert" : detail));
        }
        groups.Add(new SummaryGroup("Applications", apps));

        // Network
        var wifiCount = s.SelectedWifi.Count;
        string WifiDetail()
        {
            var selected = Main.Wifi.Items.Where(i => i.IsSelected).ToList();
            var parts = new List<string>();
            if (selected.Count(i => i.KeyBlocked) is var blocked and > 0) parts.Add($"{blocked} clé(s) non accessible(s)");
            if (selected.Count(i => i.Info.KeyViaAdmin) is var admin and > 0) parts.Add($"{admin} clé(s) récupérée(s) en administrateur");
            return string.Join(" · ", parts);
        }
        groups.Add(new SummaryGroup("Réseau",
        [
            wifiCount > 0
                ? new SummaryLine(MarkKind.Ok, Format.Plural(wifiCount, "profil Wi-Fi", "profils Wi-Fi"),
                    WifiDetail())
                : new SummaryLine(MarkKind.Off, "Profils Wi-Fi", Main.Wifi.Items.Count == 0 ? "aucun profil" : "aucun sélectionné"),
            s.SelectedNetworkDrives.Count > 0
                ? new SummaryLine(MarkKind.Ok, Format.Plural(s.SelectedNetworkDrives.Count, "lecteur réseau", "lecteurs réseau"),
                    string.Join(", ", s.NetworkDrives.Where(d => s.SelectedNetworkDrives.Contains(d.Letter)).Select(d => $"{d.Letter} {d.RemotePath}")) + " · sans mot de passe")
                : new SummaryLine(MarkKind.Off, "Lecteurs réseau", !Main.NetworkDrives.IsLoaded ? "détection…" : s.NetworkDrives.Count == 0 ? "aucun" : "aucun sélectionné"),
        ]));

        // Windows
        groups.Add(new SummaryGroup("Windows",
        [
            s.Wallpaper is { Exists: true } && s.IncludeWallpaper
                ? new SummaryLine(MarkKind.Ok, "Fond d'écran", SizeOf(JobTotals(j => j.Category == Categories.Windows)))
                : new SummaryLine(MarkKind.Off, "Fond d'écran", s.Wallpaper?.Exists == true ? "non inclus" : "non détecté"),
        ]));

        // Printers (2.1)
        var printers = Main.Printers.Items.Where(i => i.IsSelected).ToList();
        groups.Add(new SummaryGroup("Imprimantes",
        [
            printers.Count > 0
                ? new SummaryLine(MarkKind.Ok, Format.Plural(printers.Count, "imprimante", "imprimantes"), string.Join(", ", printers.Select(p => p.Name)))
                : new SummaryLine(MarkKind.Off, "Imprimantes", !Main.Printers.IsLoaded ? "détection…" : Main.Printers.Items.Count == 0 ? "aucune" : "aucune sélectionnée"),
        ]));

        Groups = groups;

        var warnings = new List<string>();
        foreach (var b in Main.Browsers.Cards.Where(c => c.IsRunning && c.Model.Include))
            warnings.Add($"{b.Name} est ouvert : il sera fermé complètement au lancement de la copie, après votre confirmation.");
        warnings.AddRange(Main.Destination.Warnings);
        if (HasCloudFiles && !SkipCloudOnly)
            warnings.Add($"Attention : {Format.Size(Sum!.Value.CloudBytes)} vont être téléchargés depuis le cloud pendant la copie.");
        if (s.OutlookMailbox is { } mb)
        {
            warnings.Add(Core.Outlook.OutlookSession.IsOutlookRunning()
                ? $"La boîte Outlook « {mb.StoreName} » est exportée après les fichiers, dans un nouveau PST (Outlook doit rester ouvert). Sa taille n'est pas comprise dans l'estimation."
                : "Outlook classique est fermé : ouvrez-le avant de lancer la copie, sinon la boîte aux lettres ne sera pas sauvegardée.");
        }
        Warnings = warnings;

        foreach (var name in new[]
                 {
                     nameof(DestinationText), nameof(PlannedBytesText), nameof(PlannedFilesText), nameof(FreeText), nameof(HasCloudFiles), nameof(CloudText),
                     nameof(SelectedFilesCell), nameof(SelectedBytesCell), nameof(PlannedFilesCell), nameof(PlannedBytesCell),
                     nameof(CloudFilesCell), nameof(CloudBytesCell), nameof(CloudFateText),
                     nameof(HasDestination), nameof(NothingSelected), nameof(InsufficientSpace), nameof(SpaceSelectedText), nameof(SpaceFreeText),
                     nameof(HasConflict), nameof(ConflictDetail),
                     nameof(CanStart), nameof(BlockReason), nameof(HasWarnings), nameof(IsComplete),
                 })
            OnPropertyChanged(name);

        Status = !HasDestination ? "Destination manquante" : HasConflict ? "Destination invalide"
            : InsufficientSpace ? "Espace insuffisant" : IsComplete ? $"{Format.Size(PlannedBytes)} à copier" : "Calcul…";
        IsDone = CanStart;
        CommandManager.InvalidateRequerySuggested();
    }
}
