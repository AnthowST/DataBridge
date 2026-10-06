using System.Windows.Input;
using DataBridge.App.Infrastructure;
using DataBridge.Core;
using DataBridge.Core.Profile;

namespace DataBridge.App.ViewModels;

public sealed record DetectionTile(string Glyph, string Title, string Value, string Detail, bool Ok);

public sealed class HomeViewModel : PageViewModel
{
    private IReadOnlyList<DetectionTile> _tiles = [];

    public HomeViewModel(MainViewModel main) : base(main, 0, "Aperçu", "", "Nav_Home")
    {
        StartCommand = new RelayCommand(() => Main.GoTo(Main.Destination));
        Status = "Vue d'ensemble";
    }

    public ICommand StartCommand { get; }

    public ProfileInfo Profile => Main.Profile;
    public DateTime Now => DateTime.Now;
    public bool IsElevated => Main.Profile.IsElevated;

    public string ElevationDetail => Main.Profile.Elevation switch
    {
        ElevationState.Elevated => "L'application a été lancée en administrateur. Elle n'en a pas besoin : toutes les fonctions sont prévues pour les droits de la session.",
        ElevationState.LimitedAdmin => "Compte administrateur utilisé avec son jeton filtré (UAC) : aucune élévation n'est demandée.",
        _ => "Aucune élévation n'est demandée : tout est fait avec les droits de la session Windows.",
    };

    public IReadOnlyList<DetectionTile> Tiles { get => _tiles; private set => Set(ref _tiles, value); }

    public override void OnActivated() => Refresh();

    public void Refresh()
    {
        var s = Main.Selection;
        var roots = s.PersonalRoots;
        var available = roots.Count(r => r.Available);
        // OneDrive: number of redirected Known Folders and the account(s) they really belong to (details in Dossiers personnels).
        var redirected = roots.Where(r => r.Info.Location == FolderLocation.OneDrive).ToList();
        var usedAccounts = redirected.Select(r => r.Info.OneDriveAccount).OfType<OneDriveAccount>().Distinct().ToList();
        var accountGroups = usedAccounts.Count + (redirected.Any(r => r.Info.OneDriveAccount is null) ? 1 : 0);
        var oneDriveValue = redirected.Count > 0
            ? Format.Plural(redirected.Count, "dossier redirigé", "dossiers redirigés")
            : "Aucun dossier redirigé";
        var oneDriveDetail = redirected.Count > 0
            ? accountGroups > 1 ? $"{accountGroups} comptes utilisés"
              : usedAccounts.Count == 1 ? usedAccounts[0].ShortLabel
              : OneDriveAccount.Unidentified
            : Main.OneDrive.Accounts.Count > 1 ? $"{Main.OneDrive.Accounts.Count} comptes configurés"
            : Main.OneDrive.Accounts.Count == 1 ? Main.OneDrive.Accounts[0].ShortLabel
            : Main.OneDriveRoots.Count > 0 ? "OneDrive détecté, " + OneDriveAccount.Unidentified.ToLowerInvariant()
            : "OneDrive non configuré";
        var browsers = s.Browsers.Where(b => b.Info.Detected).Select(b => b.Info.Name.Replace("Google ", "").Replace("Microsoft ", "").Replace("Mozilla ", "")).ToList();
        var wifi = Main.Wifi;

        Tiles =
        [
            new("", "Dossiers personnels", $"{available} / {roots.Count} détectés",
                string.Join(", ", roots.Where(r => r.Available).Select(r => r.Label)), available > 0),
            new("", "OneDrive",
                oneDriveValue, oneDriveDetail, true),
            new("", "Signatures Outlook", s.Signatures is { Exists: true } sig ? $"{sig.Names.Count} détectée(s)" : "Aucune",
                s.Signatures?.Path ?? "", s.Signatures?.Exists == true),
            new("", "Navigateurs", browsers.Count > 0 ? string.Join(", ", browsers) : "Aucun détecté",
                "Chrome, Edge, Firefox", browsers.Count > 0),
            new("", "Réseaux Wi-Fi", wifi.IsLoaded ? (wifi.ServiceAvailable ? $"{wifi.Items.Count} profil(s)" : "Indisponible") : "Détection…",
                wifi.Message ?? "Profils enregistrés accessibles à la session", wifi.ServiceAvailable && wifi.Items.Count > 0),
            new("", "Fond d'écran", s.Wallpaper?.Exists == true ? "TranscodedWallpaper détecté" : "Non détecté",
                s.Wallpaper?.Path ?? "", s.Wallpaper?.Exists == true),
        ];
    }
}
