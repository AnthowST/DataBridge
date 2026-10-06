# DataBridge

> Copiez, sauvegardez et restaurez vos données.

DataBridge est un utilitaire Windows permettant de sauvegarder et restaurer les principales données d'un profil
utilisateur.

Current version: **2.0.0**

| Fonction | Statut |
|---|---|
| Sauvegarde | Disponible |
| Restauration | Disponible |

## Fonctions principales

- Sauvegarde des dossiers personnels
- Sélection fichier par fichier
- Prise en charge des Known Folders et de OneDrive
- Signatures Outlook classique
- Profils Chrome, Edge et Firefox
- Profils Wi-Fi
- Fond d'écran
- Restauration sélective
- Gestion des conflits
- Sauvegardes DataBridge 2.0 avec manifest (`databridge-manifest.json`)
- Compatibilité avec les sauvegardes DataBridge 1.x
- Fonctionnement sans élévation administrateur

## Système

- Windows 10 / Windows 11
- Exécutable autonome `DataBridge.exe` (aucun .NET à installer), téléchargeable depuis les
  [Releases](https://github.com/AnthowST/DataBridge/releases)
- Fonctionne avec les droits de la session utilisateur (manifeste `asInvoker`) : aucune élévation, aucune invite UAC

Au lancement, DataBridge propose **Sauvegarder ce profil** ou **Restaurer un profil** (restauration d'une sauvegarde
DataBridge vers la session Windows actuelle). On revient à ce choix par « Accueil DataBridge » (en-tête) tant qu'aucune
copie ou restauration n'est en cours.

## Navigateurs

Les mots de passe, cookies, sessions et certaines données protégées des navigateurs peuvent être liés au compte Windows
et ne sont pas garantis lors d'une restauration sur un autre PC. DataBridge n'extrait aucun mot de passe et ne promet
pas leur restauration.

## Stack

- C#, WPF, .NET 10 ; aucune dépendance NuGet dans l'application
- P/Invoke Win32 : `SHGetKnownFolderPath`, `FindFirstFileExW`, `CopyFileExW`, `GetDiskFreeSpaceExW`,
  `IOCTL_STORAGE_QUERY_PROPERTY`, API WLAN (`wlanapi.dll`), `GetTokenInformation`, `GetFinalPathNameByHandleW`
- Icônes : Segoe Fluent Icons (repli Segoe MDL2 Assets) + icônes Explorateur par type de fichier
- Tests : xUnit (cœur), UI Automation via PowerShell 5.1 (fenêtre réelle)

## Commandes

```bash
dotnet build DataBridge.slnx
```

```bash
dotnet test tests/DataBridge.Tests
```

```bash
dotnet publish src/DataBridge.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

L'exécutable autonome (~62 Mo, sans .NET à installer, logo embarqué) est `publish\DataBridge.exe` : il suffit de le copier
sur le PC et de le lancer.

Test réel complet (PowerShell 5.1, non élevé, Release, données de test dans `D:\ProfileCopy-Tests`). Attention : il ouvre
puis **ferme de force** Chrome, Edge et Firefox de la session.

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-test/Run-V13.ps1
```

Scénarios des versions précédentes : `Run-Scenario.ps1` (V1), `Run-V11.ps1` (compteurs), `Run-V12.ps1` (navigateurs).

Restauration (2.0) — build **Debug** uniquement : les variables `DATABRIDGE_TEST_<DOSSIER>`, `DATABRIDGE_TEST_ROAMING`,
`DATABRIDGE_TEST_LOCAL` et `DATABRIDGE_TEST_NO_WALLPAPER_APPLY` redirigent la cible de la restauration vers
`D:\ProfileCopy-Tests\E2E-20` (jamais lues par le Release). Partie A : sauvegarde 2.0 puis restauration de données de
test ; partie B : sauvegarde 1.3 réelle avec Chrome / Edge / Firefox ouverts (**fermés de force**).

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-test/Run-V20.ps1 -Part A
```

Contrôles du Release (droits, UAC, lecture des sauvegardes 2.0 et 1.x) : `Check-V20-Release.ps1`. Import Wi-Fi réel avec un
profil jetable puis suppression : `Test-WifiImport.ps1`. Fond d'écran appliqué puis remis : `Test-WallpaperRestore.ps1`.

Stabilité du layout (1.3.2) : `Run-V132-Layout.ps1` coche / décoche chaque case (Signatures, Navigateurs, Wi-Fi, Fond
d'écran) et compare avant / après les positions UI Automation et les pixels de la page (`-Monitor100` : passe à 100 %).

```bash
powershell -NoProfile -ExecutionPolicy Bypass -File tools/ui-test/Run-V132-Layout.ps1
```

## Structure

```
src/DataBridge.Core/            logique, sans WPF
  AppInfo.cs                     nom du produit, baseline, préfixe des sauvegardes
  Native/NativeMethods.cs        déclarations Win32
  IO/                            FastDirectory (énumération streaming, attributs cloud, liens), PathUtil, PathIdentity
  Profile/                       ProfileInfo (PC, session, élévation), KnownFolders, OneDriveAccounts (comptes + mapping)
  Selection/                     SelectionTree (règles hiérarchiques + tri-état), Coverage (anti-doublons), BackupSelection
  Scanning/                      SizeScanner (tailles en arrière-plan), DirectoryListingCache (lazy loading)
  Detection/                     VolumeService, BrowserDetector, BrowserProcessService, FirefoxProfiles, WifiService…
  Copy/                          CopyPlan + BackupNaming, PlanCalculator, CopyEngine, CopyModels, DestinationGuard
  Logging/CopyLogWriter.cs       copy-log.txt
  Restore/                       restauration (2.0) : BackupManifest (+ BackupManifestWriter), BackupReader (manifest / 1.x),
                                 RestoreTarget, RestoreSelection, RestorePlanner (plan, validation, analyse), RestoreWalker,
                                 RestoreEngine, RestorePaths (sécurité des chemins), BrowserRestoreService,
                                 WifiRestoreService, WallpaperRestoreService, RestoreLogWriter
src/DataBridge.App/             WPF (assembly « DataBridge » → DataBridge.exe)
  Assets/Branding/               logo officiel databrige-logo.png (ressource WPF embarquée)
  Assets/Branding/databridge-icon.ico  icône officielle (16 à 256 px) : exe, fenêtres, barre des tâches, Alt+Tab
  Themes/Colors.xaml             charte DataBridge (couleurs de marque + jetons sémantiques)
  Themes/Theme.xaml              styles (boutons, cartes, badges, cases, navigation, recherche, progression…)
  Infrastructure/                MVVM, icônes shell, BrandAssets (chargement du logo)
  ViewModels/, Views/            ShellViewModel (accueil Sauvegarder / Restaurer), MainViewModel (assistant de sauvegarde),
                                 RestoreViewModel + Restore*ViewModel (assistant de restauration), WizardView (barre
                                 latérale + pied communs), DataBridgeDialog (modale unique)
tests/DataBridge.Tests/         tests unitaires du cœur
tools/ui-test/                   UiTest.psm1 (UI Automation, captures, jeton), scénarios Run-*.ps1
docs/screenshots/databridge-2.0.0/ captures de la fenêtre réelle (2.0.0)
```

## Identité visuelle

Toutes les couleurs sont centralisées dans `Themes/Colors.xaml` ; les vues n'utilisent que des jetons.

| Rôle | Jeton | Valeur |
|---|---|---|
| Marque — bleu nuit (structure, titres, texte fort) | `DataBridgeNavy` / `TextStrong` | `#00214D` |
| Marque — bleu vif (sélection, navigation, focus, action principale, progression) | `DataBridgeBlue` / `SelectionAccent`, `PrimaryAction` | `#0074FB` |
| Marque — turquoise (petits accents positifs, badges) | `DataBridgeTeal` / `TealAccent` | `#00D3C5` |
| Fonds | `BackgroundPrimary` / `BackgroundSecondary` | `#FFFFFF` / `#F5F7FA` |
| Texte | `TextPrimary` / `TextSecondary` | `#0B1E3D` / `#53637C` |
| États | `SuccessAccent` / `WarningAccent` / `ErrorAccent` | vert / ambre / rouge |

Dégradé `#0074FB → #00D3C5` réservé à de fins éléments (ligne d'accent, barre de progression, bandeaux de cartes).
Cases à cocher : carré 20×20 dans une cellule de taille fixe, bordure constante (1.5), coche blanche et **tiret** blanc pour
l'état partiel, centrés par `Viewbox` (nets à 100 / 125 / 150 %). Les états ne changent que couleurs, visibilité (`Hidden`)
et opacité ; l'anneau de focus clavier est un `FocusVisualStyle` (couche d'adorners) : cocher ne déplace jamais rien.
Les compteurs des cartes navigateurs ont une largeur fixe et leurs deux lignes sont toujours réservées.

## Principes

- **Sélection indépendante de l'UI** : chaque dossier personnel est un `SelectionTree` (défaut de la racine + règles
  inclure/exclure, la plus spécifique gagne, forme canonique). Seuls les choix du technicien sont stockés.
- **Une seule source de vérité** : `BackupSelection.BuildPlan()` produit des `CopyJob` utilisés à la fois par
  l'estimation (`PlanCalculator`) et par la copie (`CopyEngine`).
- **Anti-doublons** : un élément ajouté manuellement et déjà inclus dans un dossier personnel n'est pas recopié ;
  le dossier de sauvegarde est toujours exclu de la copie.
- **Cloud** : un fichier OneDrive/iCloud « disponible uniquement en ligne » est détecté par ses attributs (sans
  téléchargement), affiché à part (« 522 Mo locaux / 431.6 Go en ligne uniquement ») et, par défaut, non téléchargé.
- **Compteurs séparés** : sélectionnés / prévus / copiés / cloud non téléchargés / ignorés / en erreur. Au lancement, la
  copie recompte exactement les fichiers prévus ; le journal et l'écran final lisent le même `CopyResult`
  (contrôle : prévus = copiés + en erreur).
- **Destination récursive** : `DestinationGuard` bloque le lancement si le dossier de sauvegarde est dans une source
  sélectionnée (ou l'inverse), avec comparaison par identité de chemin (`..`, casse, `\\?\`, lecteur réseau ↔ UNC, jonctions).
- **Comptes OneDrive (1.3.1)** : `OneDriveAccountService` lit uniquement des métadonnées locales non sensibles
  (`HKCU\Software\Microsoft\OneDrive\Accounts\<Personal|BusinessN>` : `UserFolder`, `UserEmail`, `DisplayName`,
  `UserName`, `Business`, dossiers de bibliothèques synchronisées ; variables `OneDrive*`). Aucun jeton, aucun appel réseau.
  Un dossier personnel est associé au compte dont la racine locale le contient, en prenant la racine **la plus longue** ;
  sinon « Compte non identifié » (jamais deviné). Affichage : `OneDrive · Organisation` + e-mail, avec repli
  e-mail seul → organisation seule → « Compte non identifié ». Le journal liste comptes et association sans e-mail.
- **Fermeture des navigateurs** : au clic « Lancer la copie », si des navigateurs **sélectionnés** sont ouverts, la modale
  DataBridge demande confirmation ; `BrowserProcessService` ferme alors chrome.exe / msedge.exe / firefox.exe de la
  **session courante** avec leurs processus enfants (`Process.Kill(entireProcessTree)`, sans élévation), vérifie leur
  disparition (quelques essais courts), puis la copie démarre. `msedgewebview2.exe` n'est jamais visé.

## Restauration (2.0)

- **Choix de la sauvegarde** : `BackupReader` lit `databridge-manifest.json` (schéma versionné ; un schéma plus récent est
  refusé proprement) ou, pour les sauvegardes 1.x (`DataBridge_`, `itelium_ProfileCopy_`, `ProfileCopy_`), reconstruit les
  métadonnées depuis la structure et `copy-log.txt` (« Sauvegarde DataBridge ancienne génération »).
- **Destination = session actuelle** : dossiers personnels → Known Folders actuels (y compris redirigés vers OneDrive,
  avec le compte affiché) ; jamais les anciens chemins absolus. Éléments supplémentaires : chemin d'origine s'il est
  valide, adapté au profil actuel s'il était dans l'ancien profil, sinon « Destination à choisir » (jamais deviné).
- **Plan unique** : `RestorePlanner.BuildPlan` → `RestorePlan` (éléments source → destination) ; le résumé, l'analyse
  (fichiers absents / existants / remplacés / identiques, espace par volume) et le moteur utilisent les mêmes règles.
- **Conflits** : une règle globale (par défaut « remplacer uniquement si la sauvegarde est plus récente »), comparaison
  par nom, taille et date, aucune fenêtre par fichier. Un fichier remplacé est écrit à côté puis permuté.
- **Navigateurs** : profil sauvegardé → profil de ce PC choisi ; « Local State » du PC conservé ; mots de passe, cookies,
  jetons, données de synchronisation non restaurés (non portables) ; copie de sécurité des fichiers remplacés dans
  `%LOCALAPPDATA%\DataBridge\RestoreSafety` ; Firefox : profil existant ou nouveau profil inscrit dans profiles.ini.
- **Wi-Fi** : import par l'API WLAN avec les droits de la session ; profil existant conservé ou remplacé ; clé jamais
  affichée ni journalisée. **Fond d'écran** : copie, puis application par Windows (sinon « actualisation manuelle »).
- **Sécurité** : chemins relatifs stricts (pas de `..`, de chemin absolu, de flux, de nom réservé), liens / jonctions de
  la sauvegarde jamais suivis, chaque écriture vérifiée sous sa destination, dossiers système refusés.
- **Journal** : `%LOCALAPPDATA%\DataBridge\Logs\restore-AAAA-MM-JJ_HH-mm.log` (la sauvegarde n'est jamais modifiée).

## Structure de la sauvegarde

```
DataBridge_<PC>_<Session>_<AAAA-MM-JJ>_<HH-mm>\
├── Personal\ (Desktop, Documents, Downloads, Pictures, Videos, Music)
├── Additional\            éléments ajoutés (origine dans copy-log.txt)
├── Outlook\Signatures\
├── Browsers\ (Chrome\User Data, Edge\User Data, Firefox)
├── Network\WiFi\          Wi-Fi-<profil>.xml
├── Windows\Wallpaper\TranscodedWallpaper
├── copy-log.txt
└── databridge-manifest.json   (2.0) description structurée, sans mot de passe, clé, jeton ni cookie
```

Les sauvegardes des versions précédentes (`ProfileCopy_…`, `itelium_ProfileCopy_…`) restent reconnues :
`BackupNaming.LooksLikeBackup()` s'appuie sur le contenu (`copy-log.txt` signé « Journal de sauvegarde du profil »),
pas seulement sur le nom du dossier.

## Hors périmètre de cette version

Transfert PC→PC direct, restauration à distance, compte / cloud DataBridge, assistant de migration, PST/OST, profils Outlook, imprimantes,
comptes Microsoft 365, export de mots de passe, applications, licences, registre, compression, chiffrement, cloud,
serveur, installateur, mise à jour automatique, mode administrateur.
