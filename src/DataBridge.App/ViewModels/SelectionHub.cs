using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Selection;

namespace DataBridge.App.ViewModels;

/// <summary>
/// One of the four steps shown at the top of a wizard (2.2): 1. Sauvegarder / Restaurer, 2. Où / Sauvegarde, 3. Quoi,
/// 4. Lancer. The detailed pages are reached from the tiles of step 3 and count as step 3.
/// </summary>
public sealed class WizardStep(int number, string title, string automationId, Action go) : ObservableObject
{
    private bool _isCurrent;
    private bool _isDone;
    private bool _canGo = true;

    public int Number { get; } = number;
    public string Title { get; } = title;
    public string AutomationId { get; } = automationId;
    public bool IsFirst => Number == 1;
    public bool IsLast => Number == 4;
    public ICommand GoCommand { get; } = new RelayCommand(go);
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
    /// <summary>The step can be opened by a click (never during a copy / restore, never before a backup is chosen).</summary>
    public bool CanGo { get => _canGo; set => Set(ref _canGo, value); }
}

/// <summary>
/// A category of step 3 ("Quoi"): one checkbox for the whole category, a short state read from its detailed page, an
/// optional point of attention (choice still to make) and a link to the detailed page.
/// </summary>
public sealed class HubTile : ObservableObject
{
    private readonly Func<bool?>? _state;
    private readonly Action<bool>? _set;
    private readonly Func<bool> _available;
    private readonly Func<string?>? _attention;
    private readonly Func<string>? _detail;

    public HubTile(WizardPage page, string title, string glyph, Action open, Func<bool?>? state, Action<bool>? set, Func<bool> available,
        Func<string?>? attention = null, string openLabel = "Personnaliser", Func<string>? detail = null)
    {
        Page = page;
        Title = title;
        Glyph = glyph;
        OpenLabel = openLabel;
        _state = state;
        _set = set;
        _available = available;
        _attention = attention;
        _detail = detail;
        OpenCommand = new RelayCommand(open, () => IsAvailable);
    }

    public WizardPage Page { get; }
    public string Title { get; }
    public string Glyph { get; }
    public string OpenLabel { get; }
    public ICommand OpenCommand { get; }
    /// <summary>Same id as the former sidebar entry of the page ("Nav_Wifi"...), so the UI scripts still find it.</summary>
    public string AutomationId => Page.AutomationId;
    public bool HasCheckBox => _state != null;
    public bool IsAvailable => _available();
    public string Detail => _detail?.Invoke() ?? Page.Status;
    public string? Attention => IsAvailable && IsChecked != false ? _attention?.Invoke() : null;
    public bool HasAttention => !string.IsNullOrEmpty(Attention);

    /// <summary>Checked / partial (null) / unchecked. A click on a partial category checks it entirely.</summary>
    public bool? IsChecked
    {
        get => _state is null ? false : _state(); // null = partial: never coalesced to "unchecked"
        set
        {
            if (_set is null || !IsAvailable) return;
            var current = _state!();
            _set(current is null || value == true);
            Refresh();
        }
    }

    public void Refresh()
    {
        foreach (var n in new[] { nameof(IsChecked), nameof(IsAvailable), nameof(Detail), nameof(Attention), nameof(HasAttention) })
            OnPropertyChanged(n);
    }

    /// <summary>Tri-state of a set of items: all, none or some.</summary>
    public static bool? StateOf(int selected, int total) => selected == 0 ? false : selected >= total ? true : null;
}

/// <summary>Step 3 of both wizards: every category on one page, already checked, with a link to its detailed page.</summary>
public abstract class SelectionHubBase(string title, string subtitle, string automationId) : WizardPage(0, "Quoi", "", automationId)
{
    public string PageTitle { get; } = title;
    public string Subtitle { get; } = subtitle;
    public IReadOnlyList<HubTile> Tiles { get; protected set; } = [];
    /// <summary>Restore: categories the backup does not contain (their tiles are not shown).</summary>
    public virtual string AbsentText => "";
    public bool HasAbsent => AbsentText.Length > 0;
    /// <summary>Backup: sizes still being computed in the background.</summary>
    public virtual string ScanStatus => "";

    public void Refresh()
    {
        foreach (var t in Tiles) t.Refresh();
        OnPropertyChanged(nameof(AbsentText));
        OnPropertyChanged(nameof(HasAbsent));
    }

    public override void OnTick() => Refresh();

    /// <summary>Tri-state of several independent switches of one category (only those that apply are counted).</summary>
    protected static bool? Combine(params (bool Applies, bool On)[] parts)
    {
        var applying = parts.Where(p => p.Applies).ToList();
        return HubTile.StateOf(applying.Count(p => p.On), applying.Count);
    }

    protected static bool? RootsState(PersonalViewModel personal)
    {
        var roots = personal.Roots.Where(r => r.Available).ToList();
        if (roots.All(r => r.State == CheckState.Checked)) return true;
        if (roots.All(r => r.State == CheckState.Unchecked)) return false;
        return null;
    }
}

// ====================================================================== backup

public sealed class BackupHubViewModel : SelectionHubBase
{
    private readonly MainViewModel _main;

    public BackupHubViewModel(MainViewModel main) : base("Que voulez-vous sauvegarder ?",
        "Tout ce qui a été détecté sur ce PC est déjà coché. Décochez ce dont vous n'avez pas besoin, ou cliquez sur « Personnaliser » pour choisir plus précisément.",
        "Nav_Hub")
    {
        _main = main;
        var m = main;
        Tiles =
        [
            new HubTile(m.Personal, "Dossiers personnels", "", () => m.GoTo(m.Personal),
                () => RootsState(m.Personal), v => (v ? m.Personal.SelectAllCommand : m.Personal.SelectNoneCommand).Execute(null),
                () => m.Personal.Roots.Any(r => r.Available)),
            new HubTile(m.Additional, "Autres fichiers ou dossiers", "", () => m.GoTo(m.Additional), null, null, () => true,
                openLabel: "Ajouter…"),
            new HubTile(m.Outlook, "Outlook", "", () => m.GoTo(m.Outlook),
                () => Combine((m.Outlook.Exists, m.Outlook.Include), (true, m.Outlook.IncludeMailbox)),
                v => { if (m.Outlook.Exists) m.Outlook.Include = v; m.Outlook.IncludeMailbox = v; },
                () => true,
                () => m.Outlook.IncludeMailbox && m.Selection.OutlookMailbox is null && !m.Outlook.IsBusy
                    ? "Boîte mail : ouvrez Outlook classique, ou décochez-la dans « Personnaliser »." : null),
            new HubTile(m.Browsers, "Navigateurs", "", () => m.GoTo(m.Browsers),
                () => HubTile.StateOf(m.Browsers.Cards.Count(c => c.Model.Include), m.Browsers.Cards.Count(c => c.Detected && c.Profiles.Count > 0)), m.Browsers.SetAll,
                () => m.Browsers.Cards.Any(c => c.Detected && c.Profiles.Count > 0),
                () => m.Browsers.AnyRunning ? "Navigateur ouvert : il sera fermé au lancement, après confirmation." : null),
            new HubTile(m.Wifi, "Réseaux Wi-Fi", "", () => m.GoTo(m.Wifi),
                () => HubTile.StateOf(m.Selection.SelectedWifi.Count, m.Wifi.Items.Count), m.Wifi.SetAll,
                () => m.Wifi.Items.Count > 0,
                () => m.Wifi.Items.Count(i => i.IsSelected && i.KeyBlocked) is var n and > 0
                    ? $"{Format.Plural(n, "clé protégée", "clés protégées")} : récupérable dans « Personnaliser »." : null),
            new HubTile(m.NetworkDrives, "Lecteurs réseau", "", () => m.GoTo(m.NetworkDrives),
                () => HubTile.StateOf(m.Selection.SelectedNetworkDrives.Count, m.NetworkDrives.Items.Count), m.NetworkDrives.SetAll,
                () => m.NetworkDrives.Items.Count > 0),
            new HubTile(m.Wallpaper, "Fond d'écran", "", () => m.GoTo(m.Wallpaper),
                () => m.Wallpaper.Include, v => m.Wallpaper.Include = v, () => m.Wallpaper.Exists),
            new HubTile(m.Printers, "Imprimantes", "", () => m.GoTo(m.Printers),
                () => HubTile.StateOf(m.Selection.SelectedPrinters.Count, m.Printers.Items.Count(i => i.IsSupported)), m.Printers.SetSupported,
                () => m.Printers.Items.Any(i => i.IsSupported)),
        ];
    }

    public override string ScanStatus => _main.ScanStatus;

    /// <summary>Opening step 3 lists the Outlook mailboxes, as the Outlook page did when it was crossed (Outlook open only).</summary>
    public override void OnActivated()
    {
        _main.Outlook.OnActivated();
        _main.Browsers.RefreshCommand.Execute(null);
        Refresh();
    }

    public override void OnTick()
    {
        base.OnTick();
        OnPropertyChanged(nameof(ScanStatus));
        var n = Tiles.Count(t => t.IsAvailable && t.IsChecked != false);
        Status = $"{n} catégorie(s)";
        IsDone = n > 0;
    }
}

// ====================================================================== restore

public sealed class RestoreHubViewModel(RestoreViewModel restore) : SelectionHubBase("Que voulez-vous restaurer ?",
    "Tout ce que contient la sauvegarde est déjà coché. Décochez ce dont vous n'avez pas besoin. Une case orange signale un choix à faire : cliquez sur « Personnaliser ».",
    "R_Nav_Hub")
{
    private string _absent = "";

    public override string AbsentText => _absent;

    /// <summary>Tiles of the categories present in the backup just opened.</summary>
    public void Load()
    {
        var r = restore;
        var s = r.Selection!;
        var b = s.Backup;
        var tiles = new List<HubTile>();
        var absent = new List<string>();
        void Add(bool present, string name, Func<HubTile> tile)
        {
            if (present) tiles.Add(tile());
            else absent.Add(name);
        }

        Add(b.Personal.Count > 0, "dossiers personnels", () => new HubTile(r.Personal, "Dossiers personnels", "", () => r.GoTo(r.Personal),
            () => RootsState(r.Personal), v => (v ? r.Personal.SelectAllCommand : r.Personal.SelectNoneCommand).Execute(null), () => true,
            () => r.Personal.Roots.Any(x => x.Available && x.State != CheckState.Unchecked && x.Restore?.DestinationPath is null)
                ? "Dossier introuvable sur ce PC : voir « Personnaliser »." : null));
        Add(b.Additional.Count > 0, "autres fichiers", () => new HubTile(r.Additional, "Autres fichiers ou dossiers", "", () => r.GoTo(r.Additional),
            () => HubTile.StateOf(r.Additional.Items.Count(i => i.IsSelected), r.Additional.Items.Count), r.Additional.SetAll, () => true,
            () => r.Additional.Items.Count(i => i.NeedsDestination) is var n and > 0 ? $"{Format.Plural(n, "destination", "destinations")} à choisir." : null));
        Add(b.Signatures != null || b.Mailbox != null, "Outlook", () => new HubTile(r.Outlook, "Outlook", "", () => r.GoTo(r.Outlook),
            () => Combine((r.Outlook.HasSignatures, r.Outlook.Include), (r.Outlook.HasMailbox, r.Outlook.IncludeMailbox)),
            v => { if (r.Outlook.HasSignatures) r.Outlook.Include = v; if (r.Outlook.HasMailbox) r.Outlook.IncludeMailbox = v; }, () => true,
            () => s.Mailbox is { Include: true, TargetStoreId: null } ? "Choisissez la boîte de destination." : null));
        Add(b.Browsers.Count > 0, "navigateurs", () => new HubTile(r.Browsers, "Navigateurs", "", () => r.GoTo(r.Browsers),
            () => HubTile.StateOf(s.Browsers.Count(c => c.Selected), s.Browsers.Count), r.Browsers.SetAll, () => true,
            () => r.Browsers.Cards.SelectMany(c => c.Profiles).Count(p => p.NeedsChoice) is var n and > 0
                ? $"{Format.Plural(n, "profil", "profils")} de destination à choisir." : null));
        Add(b.Wifi.Count > 0, "Wi-Fi", () => new HubTile(r.Wifi, "Réseaux Wi-Fi", "", () => r.GoTo(r.Wifi),
            () => HubTile.StateOf(r.Wifi.Items.Count(i => i.IsSelected), r.Wifi.Items.Count), r.Wifi.SetAll, () => true,
            () => !r.Wifi.ServiceAvailable ? "Pas de Wi-Fi sur ce PC : les profils ne pourront pas être importés." : null));
        Add(b.NetworkDrives.Count > 0, "lecteurs réseau", () => new HubTile(r.NetworkDrives, "Lecteurs réseau", "", () => r.GoTo(r.NetworkDrives),
            () => HubTile.StateOf(r.NetworkDrives.Items.Count(i => i.IsSelected), r.NetworkDrives.Items.Count), r.NetworkDrives.SetAll, () => true,
            () => r.NetworkDrives.Items.Count(i => i.IsSelected && i.HasConflict) is var n and > 0
                ? $"{Format.Plural(n, "lettre déjà utilisée", "lettres déjà utilisées")} sur ce PC : non restauré(s)." : null));
        Add(b.Wallpaper != null, "fond d'écran", () => new HubTile(r.Wallpaper, "Fond d'écran", "", () => r.GoTo(r.Wallpaper),
            () => r.Wallpaper.Include, v => r.Wallpaper.Include = v, () => true));
        Add(b.Printers != null, "imprimantes", () => new HubTile(r.Printers, "Imprimantes", "", () => r.GoTo(r.Printers),
            () => HubTile.StateOf(r.Printers.Items.Count(i => i.IsSelected), r.Printers.Items.Count(i => i.IsSelectable)), r.Printers.SetAll,
            () => true,
            () => r.Printers.NeedsAdmin ? "Windows demandera une fois les droits administrateur." : null));

        Tiles = tiles;
        _absent = absent.Count == 0 ? "" : "Absent de cette sauvegarde : " + string.Join(", ", absent) + ".";
        OnPropertyChanged(nameof(Tiles));
        Refresh();
    }

    /// <summary>Opening step 3 lists the Outlook mailboxes of this PC, as the Outlook page did when it was crossed.</summary>
    public override void OnActivated()
    {
        restore.Outlook.OnActivated();
        Refresh();
    }

    public override void OnTick()
    {
        base.OnTick();
        var missing = Tiles.Count(t => t.HasAttention);
        Status = missing > 0 ? $"{missing} choix à faire" : $"{Tiles.Count(t => t.IsChecked != false)} catégorie(s)";
        IsDone = missing == 0 && Tiles.Any(t => t.IsChecked != false);
    }
}
