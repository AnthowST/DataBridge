using DataBridge.App.Infrastructure;
using DataBridge.Core.Scanning;

namespace DataBridge.App.ViewModels;

/// <summary>One section of a wizard sidebar (backup: Aperçu, 1. Destination ... 9. Copie; restore: 1. Sauvegarde ... 9. Restauration).</summary>
public abstract class WizardPage(int number, string title, string glyph, string automationId) : ObservableObject
{
    private string _status = "";
    private bool _isEnabled = true;
    private bool _isDone;

    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string AutomationId { get; } = automationId;
    public string NumberText => Number == 0 ? "" : Number.ToString();
    public bool HasNumber => Number > 0;

    /// <summary>Short status under the title in the sidebar ("E:\ · 426 Go libres", "3 signatures"...).</summary>
    public string Status { get => _status; set => Set(ref _status, value); }

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    /// <summary>Shows a green check instead of the number in the sidebar.</summary>
    public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }

    public virtual void OnActivated() { }

    /// <summary>Called by the wizard timer (~2.5 times per second) while the page is visible.</summary>
    public virtual void OnTick() { }
}

/// <summary>A page of the backup wizard.</summary>
public abstract class PageViewModel(MainViewModel main, int number, string title, string glyph, string automationId)
    : WizardPage(number, title, glyph, automationId)
{
    protected MainViewModel Main { get; } = main;
}

/// <summary>What the personal-folder explorer needs from the wizard that hosts it (backup or restore).</summary>
public interface IExplorerHost
{
    DirectoryListingCache Listings { get; }
    SizeScanner Scanner { get; }
    /// <summary>Sizes shown without the OneDrive "online only" part (backup option; always true for a restore).</summary>
    bool SkipCloudOnly { get; }
    /// <summary>A checkbox of the explorer changed: dependent pages and the estimate are refreshed.</summary>
    void ExplorerSelectionChanged();
}
