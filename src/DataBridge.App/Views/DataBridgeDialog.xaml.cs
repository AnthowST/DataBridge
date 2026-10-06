using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using DataBridge.Core;
using DataBridge.Core.Copy;
using DataBridge.Core.Detection;

namespace DataBridge.App.Views;

/// <summary>
/// The single modal dialog of DataBridge (same look everywhere): browser closing, retry, cancel confirmation,
/// closing during a copy, invalid destination, errors. It only asks: the caller acts after the answer.
/// </summary>
public partial class DataBridgeDialog : Window
{
    public enum Kind { Warning, Question, Error }

    private sealed record Model(
        string Heading, string? Intro, IReadOnlyList<string> Items, string? Warning, string? Detail, string? Question,
        string PrimaryText, string? SecondaryText, string IconGlyph, Brush IconBackground, Brush IconForeground)
    {
        public bool HasItems => Items.Count > 0;
    }

    private DataBridgeDialog(Window? owner, string automationId, Kind kind, string heading, string? intro, IReadOnlyList<string>? items,
        string? warning, string? detail, string? question, string primaryText, string? secondaryText, bool dangerousPrimary)
    {
        InitializeComponent();
        if (owner is { IsLoaded: true }) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = AppInfo.Name;
        AutomationProperties.SetAutomationId(this, automationId);

        var (glyph, background, foreground) = kind switch
        {
            Kind.Error => ("", Brush("ErrorSoft"), Brush("ErrorAccent")),
            Kind.Question => ("", Brush("SelectionAccentSoft"), Brush("SelectionAccent")),
            _ => ("", Brush("WarningSoft"), Brush("WarningAccent")),
        };
        DataContext = new Model(heading, intro, items ?? [], warning, detail, question, primaryText, secondaryText, glyph, background, foreground);

        PrimaryButton.Style = (Style)Application.Current.FindResource(dangerousPrimary ? "DangerFilledButton" : "PrimaryButton");
        // A destructive confirmation is never the default button: Enter must not close browsers or stop a copy.
        PrimaryButton.IsDefault = secondaryText is null;
        PrimaryButton.IsCancel = secondaryText is null;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    private void Primary_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Secondary_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    private static bool Show(DataBridgeDialog dialog) => dialog.ShowDialog() == true;

    // ------------------------------------------------------------------ browsers

    /// <summary>Confirmation before force-closing the selected browsers that are open. True = "Fermer et continuer".</summary>
    public static bool ConfirmBrowserClose(Window? owner, IReadOnlyList<BrowserKind> kinds, bool restore = false) => Show(restore
        ? RestoreBrowserCloseDialog(owner, kinds)
        : new DataBridgeDialog(owner,
        "BrowserCloseDialog", Kind.Warning,
        "Fermeture des navigateurs",
        $"Pour garantir une sauvegarde complète des profils, {AppInfo.Name} doit fermer les navigateurs suivants :",
        kinds.Select(BrowserProcessService.DisplayName).ToList(),
        "Les navigateurs seront fermés de force.\n" +
        "Les onglets, téléchargements, formulaires et données non enregistrées en cours peuvent être perdus.\n" +
        "Enregistrez votre travail avant de continuer.",
        null,
        "Voulez-vous fermer complètement ces navigateurs et lancer la sauvegarde ?",
        "Fermer et continuer", "Annuler", dangerousPrimary: false));

    /// <summary>Some browser processes are still running. True = "Réessayer".</summary>
    /// <summary>Restore wording of the browser closing confirmation.</summary>
    private static DataBridgeDialog RestoreBrowserCloseDialog(Window? owner, IReadOnlyList<BrowserKind> kinds) => new(owner,
        "BrowserCloseDialog", Kind.Warning,
        "Fermeture des navigateurs",
        $"Pour restaurer correctement les profils, {AppInfo.Name} doit fermer complètement les navigateurs suivants :",
        kinds.Select(BrowserProcessService.DisplayName).ToList(),
        "Les navigateurs seront fermés de force.\nLes données non enregistrées peuvent être perdues.",
        null,
        "Voulez-vous fermer complètement ces navigateurs et lancer la restauration ?",
        "Fermer et continuer", "Annuler", dangerousPrimary: false);

    public static bool RetryBrowserClose(Window? owner, IReadOnlyList<BrowserKind> remaining, bool restore = false)
    {
        if (restore)
        {
            var restoreNames = remaining.Select(BrowserProcessService.DisplayName).ToList();
            return Show(new DataBridgeDialog(owner, "BrowserRetryDialog", Kind.Error,
                restoreNames.Count == 1 ? $"Impossible de fermer {restoreNames[0]}" : "Impossible de fermer certains navigateurs",
                "Un ou plusieurs processus sont toujours actifs :",
                restoreNames,
                "Le profil ne peut pas être restauré pendant que le navigateur est ouvert.",
                "Fermez-les manuellement puis réessayez, ou annulez la restauration. Aucune élévation n'est demandée.",
                null, "Réessayer", "Annuler", dangerousPrimary: false));
        }
        var names = remaining.Select(BrowserProcessService.DisplayName).ToList();
        return Show(new DataBridgeDialog(owner, "BrowserRetryDialog", Kind.Error,
            names.Count == 1 ? $"Impossible de fermer {names[0]}" : "Impossible de fermer certains navigateurs",
            "Un ou plusieurs processus sont toujours actifs :",
            names,
            "La copie du profil pourrait être incomplète.",
            "Fermez-les manuellement puis réessayez, ou annulez la sauvegarde. Aucune élévation n'est demandée.",
            null, "Réessayer", "Annuler", dangerousPrimary: false));
    }

    // ------------------------------------------------------------------ restore

    public static bool ConfirmCancelRestore(Window? owner) => Show(new DataBridgeDialog(owner, "CancelRestoreDialog", Kind.Question,
        "Interrompre la restauration ?",
        "La restauration s'arrêtera après le fichier en cours. Les éléments déjà restaurés sont conservés et le journal sera écrit.",
        null, null, null, null, "Interrompre la restauration", "Continuer la restauration", dangerousPrimary: true));

    public static bool ConfirmCloseDuringRestore(Window? owner) => Show(new DataBridgeDialog(owner, "CloseDuringRestoreDialog", Kind.Question,
        $"Fermer {AppInfo.Name} ?",
        "Une restauration est en cours. L'interrompre et fermer l'application ? Les éléments déjà restaurés sont conservés et le journal sera écrit.",
        null, null, null, null, "Interrompre et fermer", "Continuer la restauration", dangerousPrimary: true));

    // ------------------------------------------------------------------ copy

    public static bool ConfirmCancelCopy(Window? owner) => Show(new DataBridgeDialog(owner, "CancelCopyDialog", Kind.Question,
        "Interrompre la copie ?",
        "La copie s'arrêtera après le fichier en cours. Les éléments déjà copiés sont conservés et le journal sera écrit.",
        null, null, null, null, "Interrompre la copie", "Continuer la copie", dangerousPrimary: true));

    public static bool ConfirmCloseDuringCopy(Window? owner) => Show(new DataBridgeDialog(owner, "CloseDuringCopyDialog", Kind.Question,
        $"Fermer {AppInfo.Name} ?",
        "Une copie est en cours. L'interrompre et fermer l'application ? Les éléments déjà copiés sont conservés et le journal sera écrit.",
        null, null, null, null, "Interrompre et fermer", "Continuer la copie", dangerousPrimary: true));

    public static void ShowDestinationConflict(Window? owner, DestinationConflict conflict) => Show(new DataBridgeDialog(owner,
        "DestinationConflictDialog", Kind.Error,
        DestinationConflict.Title,
        DestinationConflict.Message + "\n\n" + DestinationConflict.Advice,
        null, null, conflict.Detail, null, "Compris", null, dangerousPrimary: false));

    public static void ShowError(Window? owner, string message) => Show(new DataBridgeDialog(owner, "ErrorDialog", Kind.Error,
        "Une erreur est survenue", message, null, null, null, null, "Fermer", null, dangerousPrimary: false));
}
