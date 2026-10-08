using System.ComponentModel;
using System.Runtime.CompilerServices;
using DataBridge.Printers.Archive;
using DataBridge.Printers.Model;

namespace DataBridge.Printers.Restore;

public enum NameAction
{
    /// <summary>No printer with this name on the target: create it.</summary>
    Create,
    Skip,
    /// <summary>Keep the existing printer untouched (it may still become the default printer).</summary>
    KeepExisting,
    CreateWithOtherName,
    /// <summary>Delete the existing printer, then create the archived one (explicit choice only).</summary>
    Replace,
}

public enum PortAction
{
    Create,
    /// <summary>A port with the same name and the same configuration exists: use it.</summary>
    ReuseIdentical,
    /// <summary>Use the existing port as it is, although its configuration differs.</summary>
    KeepExisting,
    CreateWithOtherName,
    /// <summary>Change the existing port's configuration (only when no other printer uses it).</summary>
    Reconfigure,
    NotNeeded,
}

public enum DriverAction
{
    UseInstalled,
    InstallFromArchive,
    InstallInbox,
    InstallUserPackage,
    /// <summary>Connections: let the print server provide the driver.</summary>
    FromServer,
    None,
}

public enum IssueLevel { Info, Warning, Blocking }

public sealed record PlanIssue(IssueLevel Level, string Text);

public sealed class PrinterPlan : INotifyPropertyChanged
{
    public PrinterEntry Entry { get; init; } = new();
    public PortEntry? Port { get; init; }
    public DriverEntry? Driver { get; init; }

    /// <summary>Problems that do not depend on the user's choices (support level, missing data…).</summary>
    public List<PlanIssue> FixedIssues { get; } = new();

    // Name
    public bool NameConflict { get; set; }
    public string? ExistingPrinterInfo { get; set; }
    public bool ExistingIsConnection { get; set; }
    public List<NameAction> NameChoices { get; set; } = new() { NameAction.Create };

    // Port
    public bool PortConflict { get; set; }
    public string? ExistingPortInfo { get; set; }
    public List<string> PortUsedBy { get; set; } = new();
    public List<PortAction> PortChoices { get; set; } = new() { PortAction.NotNeeded };

    // Driver
    public string? InstalledDriverVersion { get; set; }
    public bool DriverVersionDiffers { get; set; }
    public bool DriverPackageUsable { get; set; }
    public List<string> DriverUsedBy { get; set; } = new();
    public List<DriverAction> DriverChoices { get; set; } = new() { DriverAction.None };
    public string DriverStatusText { get; set; } = "";

    // Security
    public SecurityPlan? Security { get; set; }

    private bool _selected;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    private NameAction _nameAction;
    public NameAction NameAction
    {
        get => _nameAction;
        set
        {
            var previous = _nameAction;
            Set(ref _nameAction, value);
            // Choosing an action for a conflict means "restore this one"; "Skip" means the opposite.
            if (value == NameAction.Skip) Selected = false;
            else if (previous == NameAction.Skip && IsSupported) Selected = true;
            Changed();
        }
    }

    private string _newName = "";
    public string NewName { get => _newName; set { Set(ref _newName, value); Changed(); } }

    private PortAction _portAction;
    public PortAction PortAction { get => _portAction; set { Set(ref _portAction, value); Changed(); } }

    private string _newPortName = "";
    public string NewPortName { get => _newPortName; set { Set(ref _newPortName, value); Changed(); } }

    private DriverAction _driverAction;
    public DriverAction DriverAction { get => _driverAction; set { Set(ref _driverAction, value); Changed(); } }

    private string? _userInfPath;
    public string? UserInfPath { get => _userInfPath; set { Set(ref _userInfPath, value); Changed(); } }

    public bool RestoreGlobalSettings { get; set; } = true;
    public bool RestoreUserSettings { get; set; } = true;
    public bool RestoreDriverData { get; set; } = true;
    public bool RestoreShare { get; set; } = true;
    public bool RestoreSecurity { get; set; } = true;

    private bool _mapLocalAccounts;
    public bool MapLocalAccountsByName { get => _mapLocalAccounts; set { Set(ref _mapLocalAccounts, value); Changed(); } }

    public bool MakeDefault { get; set; }

    public bool IsConnection => Entry.Kind == PrinterKind.SharedConnection;
    public string KindText => Util.Labels.Kind(Entry.Kind);
    public bool IsSupported => Entry.Support == SupportLevel.Supported;

    public string TargetName => NameAction == NameAction.CreateWithOtherName && !string.IsNullOrWhiteSpace(NewName) ? NewName.Trim() : Entry.Name;

    public string? TargetPortName => PortAction switch
    {
        PortAction.CreateWithOtherName => string.IsNullOrWhiteSpace(NewPortName) ? null : NewPortName.Trim(),
        PortAction.NotNeeded => null,
        _ => Port?.Name,
    };

    /// <summary>True when the chosen actions need the elevated worker.</summary>
    public bool RequiresAdmin =>
        Selected && NameAction != NameAction.Skip &&
        (!IsConnection
            ? NameAction != NameAction.KeepExisting || DriverAction is DriverAction.InstallFromArchive or DriverAction.InstallInbox or DriverAction.InstallUserPackage
            : DriverAction is DriverAction.InstallFromArchive or DriverAction.InstallUserPackage);

    /// <summary>All issues for the current choices; any Blocking issue prevents the restoration of this printer.</summary>
    public List<PlanIssue> Issues
    {
        get
        {
            var list = new List<PlanIssue>(FixedIssues);
            if (!IsSupported) return list;
            if (NameAction == NameAction.CreateWithOtherName)
            {
                if (string.IsNullOrWhiteSpace(NewName)) list.Add(new(IssueLevel.Blocking, "Indiquez le nouveau nom de l'imprimante."));
                else if (NewName.Contains('\\') || NewName.Contains(',') || NewName.Length > 220) list.Add(new(IssueLevel.Blocking, "Nom d'imprimante invalide (pas de « \\ » ni de « , »)."));
            }
            if (NameAction == NameAction.Replace)
                list.Add(new(IssueLevel.Warning, "L'imprimante existante sera supprimée puis recréée (son état précédent est noté dans le journal, mais la suppression n'est pas annulable automatiquement)."));
            if (!IsConnection && NameAction != NameAction.Skip && NameAction != NameAction.KeepExisting)
            {
                if (PortAction == PortAction.CreateWithOtherName && string.IsNullOrWhiteSpace(NewPortName))
                    list.Add(new(IssueLevel.Blocking, "Indiquez le nom du nouveau port."));
                if (PortAction == PortAction.KeepExisting)
                    list.Add(new(IssueLevel.Warning, $"Le port existant sera utilisé tel quel : {ExistingPortInfo}."));
                if (PortAction == PortAction.Reconfigure && PortUsedBy.Count > 0)
                    list.Add(new(IssueLevel.Blocking, $"Le port est utilisé par : {string.Join(", ", PortUsedBy)}. Il ne peut pas être reconfiguré."));
            }
            if (NameAction != NameAction.Skip && NameAction != NameAction.KeepExisting)
            {
                if (DriverAction == DriverAction.InstallUserPackage && string.IsNullOrWhiteSpace(UserInfPath))
                    list.Add(new(IssueLevel.Blocking, "Sélectionnez le fichier INF d'un package de pilote compatible."));
                if (DriverAction == DriverAction.None && !IsConnection)
                    list.Add(new(IssueLevel.Blocking, "Aucun pilote disponible : sélectionnez un package compatible."));
                if (DriverAction == DriverAction.InstallFromArchive && DriverVersionDiffers && DriverUsedBy.Count > 0)
                    list.Add(new(IssueLevel.Warning, $"Installer la version {Driver?.Version} remplacera la version {InstalledDriverVersion} aussi pour : {string.Join(", ", DriverUsedBy)}."));
                if (DriverAction == DriverAction.UseInstalled && DriverVersionDiffers)
                    list.Add(new(IssueLevel.Warning, $"Le pilote installé (version {InstalledDriverVersion}) diffère de celui de la source ({Driver?.Version}) : les options constructeur seront restaurées partiellement."));
            }
            if (Security != null && RestoreSecurity && !IsConnection)
                foreach (var note in Security.Notes) list.Add(new(IssueLevel.Warning, note));
            return list;
        }
    }

    public bool CanRestore => IsSupported && NameAction != NameAction.Skip && !Issues.Any(i => i.Level == IssueLevel.Blocking);

    public string StatusSummary
    {
        get
        {
            if (!IsSupported) return "Non restaurable : " + (Entry.SupportNote ?? "type non pris en charge");
            var blocking = Issues.FirstOrDefault(i => i.Level == IssueLevel.Blocking);
            if (blocking != null) return "Bloquée : " + blocking.Text;
            var parts = new List<string>();
            if (NameConflict) parts.Add("nom déjà utilisé");
            if (PortConflict) parts.Add("port en conflit");
            if (DriverVersionDiffers) parts.Add("autre version du pilote");
            if (Issues.Any(i => i.Level == IssueLevel.Warning)) parts.Add("avertissements");
            return parts.Count == 0 ? "Prête" : "Conflit : " + string.Join(", ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Notifies the computed properties after a choice changed.</summary>
    public void Changed()
    {
        foreach (var n in new[] { nameof(Issues), nameof(CanRestore), nameof(StatusSummary), nameof(RequiresAdmin), nameof(TargetName) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class RestorePlan
{
    public OpenedArchive Archive { get; init; } = null!;
    public List<PrinterPlan> Printers { get; } = new();
    public List<PlanIssue> GlobalIssues { get; } = new();
    public SourceInfo Target { get; init; } = new();
    public bool SpoolerRunning { get; set; }
    public bool RunsAsSessionUser { get; init; }
    public string ProcessUser { get; init; } = "";
    public string? SessionUser { get; init; }
    /// <summary>Restore "LegacyDefaultPrinterMode = 1" (the user, not Windows, manages the default printer).</summary>
    public bool RestoreLegacyDefaultMode { get; set; }
    public bool LegacyModeDiffers { get; set; }

    public IEnumerable<PrinterPlan> ToRestore => Printers.Where(p => p.Selected && p.CanRestore);
    public bool NeedsAdmin => ToRestore.Any(p => p.RequiresAdmin) || !SpoolerRunning;
}
