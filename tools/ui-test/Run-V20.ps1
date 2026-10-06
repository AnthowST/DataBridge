# DataBridge 2.0 — restore scenarios on the real window (Windows PowerShell 5.1, NOT elevated).
# Controlled environment: the DEBUG build redirects the Known Folders and the application data of the RESTORE side
# (DATABRIDGE_TEST_* variables, never read by the Release) to D:\ProfileCopy-Tests\E2E-20, so no real personal data,
# browser profile, signature or wallpaper of this PC is overwritten.
#   Part A — new 2.0 backup (manifest) of test data, sources modified / deleted, restore, checks (files, contents,
#            dates, counters, log).
#   Part B — real DataBridge 1.3 backup (legacy): Bureau + Chrome / Edge / Firefox profiles + signatures + wallpaper
#            restored into the test environment, with Chrome / Edge / Firefox OPEN: DataBridge modal, force close,
#            verification (msedgewebview2 untouched).
param(
    [string]$Exe = "$PSScriptRoot\..\..\src\DataBridge.App\bin\Debug\net10.0-windows\DataBridge.exe",
    [string]$Root = 'D:\ProfileCopy-Tests\E2E-20',
    [string]$Legacy = 'D:\ProfileCopy-Tests\Backups\DataBridge_PC-ANTHOW_Anthow_2026-10-06_17-11',
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots\databridge-2.0.0",
    [ValidateSet('A', 'B', 'AB')][string]$Part = 'AB'
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
$CheckCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
New-Item -ItemType Directory -Force $Shots | Out-Null
$report = New-Object System.Collections.Generic.List[string]
function Log($m) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $m; $report.Add($line); Write-Host $line }
function Val($root, [string]$id, [int]$t = 3) { try { (Find-Id $root $id $t).Current.Name } catch { '(absent)' } }
function Texts($el) { (@($el.FindAll($TS::Descendants, $All)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 1 }) -join ' | ' }
$sid = (Get-Process -Id $PID).SessionId
function CountP([string]$name) { @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid }).Count }
function Counts { "chrome=$(CountP 'chrome') msedge=$(CountP 'msedge') firefox=$(CountP 'firefox') | msedgewebview2=$(CountP 'msedgewebview2')" }
function Find-Window($owner, [string]$id, [int]$TimeoutSec = 25) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $d = $owner.FindFirst($TS::Children, $cond)
        if (-not $d) { $d = $AE::RootElement.FindFirst($TS::Children, $cond) }
        if ($d) { return $d }
        Start-Sleep -Milliseconds 300
    }
    return $null
}
function Check-Error($win) {
    $d = $win.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'ErrorDialog')))
    if ($d) { Log ("ERREUR AFFICHÉE : " + (Texts $d)); Invoke-El (Find-Id $d 'DialogPrimary') }
}
function Shot($win, [string]$name) { Start-Sleep -Milliseconds 500; Save-Shot $win (Join-Path $Shots $name) | Out-Null }
function Wait-Value($win, [string]$id, [scriptblock]$ok, [int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { $v = Val $win $id 1; if (& $ok $v) { return $v }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
    return $v
}
function Start-Test([hashtable]$envs) {
    foreach ($k in @('DESKTOP', 'DOCUMENTS', 'DOWNLOADS', 'PICTURES', 'VIDEOS', 'MUSIC', 'ROAMING', 'LOCAL', 'NO_WALLPAPER_APPLY')) {
        Remove-Item "Env:DATABRIDGE_TEST_$k" -ErrorAction SilentlyContinue
    }
    foreach ($k in $envs.Keys) { Set-Item "Env:DATABRIDGE_TEST_$k" $envs[$k] }
    $w = Start-App (Resolve-Path $Exe).Path
    Start-Sleep -Seconds 2
    return $w
}
function Open-Backup($win, [string]$path) {
    Invoke-El (Find-Id $win 'ModeRestoreButton'); Start-Sleep -Seconds 2
    Invoke-El (Find-Id $win 'ChooseBackupButton')
    Complete-FileDialog $path | Out-Null
    $v = Wait-Value $win 'BackupComputer' { param($x) $x -and $x -ne '(absent)' -and $x -ne 'inconnu' } 60
    Start-Sleep -Seconds 2
}
function Wait-RestoreResult($win, [int]$minutes = 20) {
    $deadline = (Get-Date).AddMinutes($minutes)
    while ((Get-Date) -lt $deadline) {
        try { $t = Find-Id $win 'RestoreResultTitle' 1; if ($t.Current.Name) { return $t.Current.Name } } catch { }
        Start-Sleep -Seconds 1
    }
    return '(pas de résultat)'
}
function Result-Lines($win, [string]$id) { (@((Find-Id $win $id).FindAll($TS::Descendants, $All)) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 1 }) -join ' | ' }

# ======================================================================================== Part A
if ($Part -match 'A') {
    Log '===== PARTIE A : sauvegarde 2.0 (manifest) puis restauration contrôlée ====='
    if (Test-Path "$Root\A") { Remove-Item -Recurse -Force "$Root\A" }
    $src = "$Root\A\Profil"
    $kf = @{ DESKTOP = "$src\Bureau"; DOCUMENTS = "$src\Documents"; DOWNLOADS = "$src\Telechargements"; PICTURES = "$src\Images"; VIDEOS = "$src\Videos"; MUSIC = "$src\Musique" }
    foreach ($d in $kf.Values) { New-Item -ItemType Directory -Force $d | Out-Null }
    function MakeFile([string]$path, [int]$size, [string]$text = '') {
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        if ($text) { Set-Content -Encoding UTF8 $path $text } else { [IO.File]::WriteAllBytes($path, (New-Object byte[] $size)) }
    }
    MakeFile "$src\Documents\Clients\facture-2026.xlsx" 0 'facture client A — 1 250,00 CHF'
    MakeFile "$src\Documents\Clients\devis.docx" 0 'devis 42'
    MakeFile "$src\Documents\Comptabilité\bilan.xlsx" 0 'bilan 2025'
    MakeFile "$src\Documents\Adobe\preset.xmp" 0 'preset'
    MakeFile "$src\Documents\Ancienne ISO\windows-old.iso" (8MB)
    MakeFile "$src\Documents\Projects\plan.txt" 0 'version de la sauvegarde'
    MakeFile "$src\Documents\Projects\notes.txt" 0 'notes'
    MakeFile "$src\Bureau\a-faire.txt" 0 'à faire : appeler le client'
    MakeFile "$src\Images\Vacances\photo-01.jpg" (300KB)
    MakeFile "$src\Images\Vacances\photo-02.jpg" (250KB)
    MakeFile "$src\Musique\morceau.mp3" (120KB)
    $extra = "$Root\A\Extra\Compta 2025"
    MakeFile "$extra\journal.csv" 0 'date;montant'
    MakeFile "$extra\Factures\f-001.pdf" (40KB)
    $older = (Get-Date).AddDays(-30)
    Get-ChildItem -Recurse -File $src, $extra | ForEach-Object { $_.LastWriteTime = $older.AddMinutes($_.Length % 97) }
    $originals = @{}
    Get-ChildItem -Recurse -File $src, $extra | ForEach-Object { $originals[$_.FullName] = [pscustomobject]@{ Hash = (Get-FileHash $_.FullName).Hash; Date = $_.LastWriteTimeUtc } }
    Log ("Source de test : " + $originals.Count + " fichiers")

    $targetA = "$Root\A\AppData"
    $win = Start-Test ($kf + @{ ROAMING = "$targetA\Roaming"; LOCAL = "$targetA\Local"; NO_WALLPAPER_APPLY = '1' })
    $proc = Get-Process DataBridge | Select-Object -First 1
    Log "Exe : $($proc.Path) | jeton : $([UiNative]::TokenInfo($proc.Id)) | UAC : $([bool](Get-Process consent -ErrorAction SilentlyContinue))"
    Log ("Titre : " + $win.Current.Name)
    Shot $win '01-accueil-sauvegarder-restaurer.png'

    # ----- Backup (existing 1.3.2 workflow, unchanged) -----
    Invoke-El (Find-Id $win 'ModeBackupButton'); Start-Sleep -Seconds 3
    Log ("Sauvegarde : " + $win.Current.Name + " | étapes : " + (Val $win 'StepsTitle'))
    New-Item -ItemType Directory -Force "$Root\A\Sauvegardes" | Out-Null
    Set-Destination $win "$Root\A\Sauvegardes"
    Go-Page $win 'Nav_Additional'
    Invoke-El (Find-Id $win 'AddFolderButton')
    Complete-FileDialog $extra | Out-Null
    Start-Sleep -Seconds 2
    Go-Page $win 'Nav_Browsers'; Start-Sleep -Seconds 1
    foreach ($cb in @($win.FindAll($TS::Descendants, $CheckCond))) { if ((Get-ToggleState $cb) -eq 'On') { Toggle-El $cb; Start-Sleep -Milliseconds 300 } }
    Go-Page $win 'Nav_Summary'
    $null = Wait-Value $win 'SummaryEstimated' { param($x) $x -and $x -notmatch 'calcul|Calcul' } 120
    Start-Sleep -Seconds 2
    Invoke-El (Find-Id $win 'FooterStartCopyButton')
    $deadline = (Get-Date).AddMinutes(10); $title = $null
    while (-not $title -and (Get-Date) -lt $deadline) { try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }; Start-Sleep -Seconds 1 }
    Log "Sauvegarde : $title | fichiers $(Val $win 'ResultFiles') | erreurs $(Val $win 'ResultErrors')"
    $backup = Get-ChildItem "$Root\A\Sauvegardes" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $manifest = Join-Path $backup.FullName 'databridge-manifest.json'
    Log ("Sauvegarde créée : " + $backup.FullName + " | manifest : " + (Test-Path $manifest))
    $json = Get-Content -Raw -Encoding UTF8 $manifest | ConvertFrom-Json
    Log ("Manifest : schemaVersion $($json.schemaVersion), version $($json.dataBridgeVersion), PC $($json.source.computer), catégories $($json.categories -join ', '), fichiers $($json.totals.files)")
    Log ("Manifest sans secret : " + (-not ((Get-Content -Raw $manifest) -match 'keyMaterial|password|token|cookie')))
    Copy-Item $manifest (Join-Path $Shots 'exemple-databridge-manifest.json') -Force
    Invoke-El (Find-Id $win 'FinishButton'); Start-Sleep -Seconds 2
    Log ("« Terminer » ramène à l'accueil : " + ((Val $win 'ModeChoiceTitle') -ne '(absent)'))

    # ----- Sources modified / deleted -----
    Remove-Item "$src\Documents\Clients\facture-2026.xlsx", "$src\Bureau\a-faire.txt", "$src\Documents\Ancienne ISO\windows-old.iso"
    Remove-Item -Recurse "$src\Images\Vacances", "$extra"
    Set-Content -Encoding UTF8 "$src\Documents\Projects\plan.txt" 'version modifiée après la sauvegarde (plus récente)'
    Set-Content -Encoding UTF8 "$src\Documents\Projects\notes.txt" 'ancienne version locale'
    (Get-Item "$src\Documents\Projects\notes.txt").LastWriteTime = (Get-Date).AddDays(-60)
    Log 'Sources modifiées : 5 fichiers supprimés (dont l''ISO et le dossier supplémentaire), plan.txt modifié (plus récent), notes.txt plus ancien que la sauvegarde.'

    # ----- Restore -----
    Open-Backup $win $backup.FullName
    Log ("Sauvegarde lue : PC $(Val $win 'BackupComputer') | session $(Val $win 'BackupUser') | $(Val $win 'BackupDate') | $(Val $win 'BackupVersion') | $(Val $win 'BackupGeneration') | $(Val $win 'BackupSize') | $(Val $win 'BackupFiles')")
    Shot $win '03-apercu-sauvegarde.png'
    Invoke-El (Find-Id $win 'UseBackupButton'); Start-Sleep -Seconds 3
    Check-Error $win
    $doc = Find-Row $win 'Documents'
    Log ("Documents : " + (Texts $doc))
    Shot $win '04-dossiers-personnels.png'
    Expand-Row $win 'Documents' | Out-Null
    $iso = Get-RowCheck $win 'Ancienne ISO'
    Toggle-El $iso; Start-Sleep -Seconds 1
    Log ("Documents après décochage de « Ancienne ISO » : " + (Get-ToggleState (Get-RowCheck $win 'Documents')))
    Shot $win '05-selection-partielle-documents.png'
    Go-Page $win 'R_Nav_Additional'; Start-Sleep -Seconds 1
    Log ("Supplémentaires : " + (Texts (Find-Id $win 'AdditionalItems')))
    Shot $win '06-fichiers-supplementaires.png'
    Go-Page $win 'R_Nav_Outlook'; Start-Sleep -Seconds 1
    Shot $win '07-signatures-outlook.png'
    Go-Page $win 'R_Nav_Wifi'; Start-Sleep -Seconds 1
    Shot $win '09-wifi.png'
    Go-Page $win 'R_Nav_Wallpaper'; Start-Sleep -Seconds 2
    Shot $win '10-fond-ecran.png'
    Go-Page $win 'R_Nav_Summary'
    $null = Wait-Value $win 'RestoreFilesToWrite' { param($x) $x -match '^\d' } 120
    Start-Sleep -Seconds 2
    Log ("Résumé : à restaurer $(Val $win 'RestoreVolume') | fichiers $(Val $win 'RestoreFilesToWrite') | remplacés $(Val $win 'RestoreReplaced') | existants $(Val $win 'RestoreExisting')")
    Log ("Correspondances : " + (Texts (Find-Id $win 'RestoreMappings')))
    Log ("Volumes : " + (Texts (Find-Id $win 'RestoreVolumes')))
    Shot $win '11-resume-avant-restauration.png'
    Invoke-El (Find-Id $win 'FooterStartRestoreButton')
    $result = Wait-RestoreResult $win 5
    Start-Sleep -Seconds 1
    Log "Résultat : $result"
    Log ("Chiffres : restaurés $(Val $win 'RestoreResultFiles') | volume $(Val $win 'RestoreResultBytes') | remplacés $(Val $win 'RestoreResultReplaced') | conservés $(Val $win 'RestoreResultKept') | erreurs $(Val $win 'RestoreResultErrors') | durée $(Val $win 'RestoreResultDuration')")
    Log ("Contrôle : " + (Val $win 'RestoreResultControl'))
    Log ("Données : " + (Result-Lines $win 'RestoreResultLines'))
    Log ("Wi-Fi : " + (Result-Lines $win 'RestoreResultWifi') + " | Windows : " + (Result-Lines $win 'RestoreResultWindows'))
    $logPath = (Val $win 'RestoreLogPath') -replace '^Journal : ', ''
    Log "Journal : $logPath"
    Shot $win '14-restauration-terminee.png'

    # ----- Checks -----
    $back = 0; $same = 0; $dates = 0
    foreach ($f in "$src\Documents\Clients\facture-2026.xlsx", "$src\Bureau\a-faire.txt", "$src\Images\Vacances\photo-01.jpg", "$src\Images\Vacances\photo-02.jpg", "$extra\journal.csv", "$extra\Factures\f-001.pdf") {
        if (Test-Path $f) { $back++; if ((Get-FileHash $f).Hash -eq $originals[$f].Hash) { $same++ }; if ((Get-Item $f).LastWriteTimeUtc -eq $originals[$f].Date) { $dates++ } }
    }
    Log "Fichiers supprimés revenus : $back / 6 | contenu identique : $same / 6 | date de modification conservée : $dates / 6"
    Log ("ISO décochée non restaurée : " + (-not (Test-Path "$src\Documents\Ancienne ISO\windows-old.iso")))
    Log ("plan.txt (plus récent localement) conservé : " + ((Get-Content -Raw "$src\Documents\Projects\plan.txt") -match 'modifiée'))
    Log ("notes.txt (plus ancien localement) remplacé : " + ((Get-FileHash "$src\Documents\Projects\notes.txt").Hash -eq $originals["$src\Documents\Projects\notes.txt"].Hash))
    $sig = Get-ChildItem "$targetA\Roaming\Microsoft\Signatures" -Recurse -File -ErrorAction SilentlyContinue
    Log ("Signatures restaurées dans l'environnement de test : " + @($sig).Count + " fichier(s) ; vraies signatures inchangées")
    Log ("Fond d'écran restauré (fichier, sans application en test) : " + (Test-Path "$targetA\Roaming\Microsoft\Windows\Themes\TranscodedWallpaper"))
    if (Test-Path $logPath) {
        $log = Get-Content -Raw -Encoding UTF8 $logPath
        Log ("Journal : en-tête « Journal de restauration » : $($log -match 'Journal de restauration') | correspondances : $($log -match 'Sauvegarde   : Personal\\Documents') | clé Wi-Fi absente : $(-not ($log -match 'keyMaterial'))")
    }
    Invoke-El (Find-Id $win 'RestoreFinishButton'); Start-Sleep -Seconds 2
    Log ("« Terminer » ramène à l'accueil : " + ((Val $win 'ModeChoiceTitle') -ne '(absent)'))
    (Get-Process DataBridge -ErrorAction SilentlyContinue) | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(5000) | Out-Null }
}

# ======================================================================================== Part B
if ($Part -match 'B') {
    Log '===== PARTIE B : sauvegarde 1.3 réelle (ancienne génération) + navigateurs ouverts ====='
    if (Test-Path "$Root\B") { Remove-Item -Recurse -Force "$Root\B" }
    $tb = "$Root\B\Profil"
    $kfb = @{ DESKTOP = "$tb\Bureau"; DOCUMENTS = "$tb\Documents"; DOWNLOADS = "$tb\Telechargements"; PICTURES = "$tb\Images"; VIDEOS = "$tb\Videos"; MUSIC = "$tb\Musique" }
    foreach ($d in $kfb.Values) { New-Item -ItemType Directory -Force $d | Out-Null }
    $targetB = "$Root\B\AppData"

    Log "Ouverture de Chrome, Edge et Firefox…"
    Start-Process 'C:\Program Files\Google\Chrome\Application\chrome.exe' 'about:blank' | Out-Null
    Start-Process 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' 'about:blank' | Out-Null
    Start-Process 'C:\Program Files\Mozilla Firefox\firefox.exe' 'about:blank' | Out-Null
    Start-Sleep -Seconds 8
    Log ("Processus avant : " + (Counts))

    $win = Start-Test ($kfb + @{ ROAMING = "$targetB\Roaming"; LOCAL = "$targetB\Local"; NO_WALLPAPER_APPLY = '1' })
    Open-Backup $win $Legacy
    Log ("Sauvegarde 1.3 lue : $(Val $win 'BackupGeneration') | PC $(Val $win 'BackupComputer') | $(Val $win 'BackupVersion') | catégories : $(Texts (Find-Id $win 'BackupCategories'))")
    Shot $win '02-selection-sauvegarde-ancienne-generation.png'
    Invoke-El (Find-Id $win 'UseBackupButton'); Start-Sleep -Seconds 3
    # Only Bureau among the personal folders (the others are large and already covered by part A).
    Invoke-El (Find-Id $win 'SelectNoneButton'); Start-Sleep -Milliseconds 500
    Toggle-El (Get-RowCheck $win 'Bureau'); Start-Sleep -Seconds 1
    Go-Page $win 'R_Nav_Browsers'; Start-Sleep -Seconds 2
    $profiles = @(Find-All $win 'RestoreProfileCheck' | ForEach-Object { $_.Current.Name + ' [' + (Get-ToggleState $_) + ']' })
    $targets = @(Find-All $win 'RestoreTargetSummary' | ForEach-Object { $_.Current.Name })
    for ($i = 0; $i -lt $profiles.Count; $i++) { Log ("Profil sauvegardé " + $profiles[$i] + " " + $targets[$i]) }
    Shot $win '08-navigateurs-correspondance.png'
    Go-Page $win 'R_Nav_Summary'
    $null = Wait-Value $win 'RestoreFilesToWrite' { param($x) $x -match '^\d' } 180
    Start-Sleep -Seconds 2
    Log ("Résumé : à restaurer $(Val $win 'RestoreVolume') | fichiers $(Val $win 'RestoreFilesToWrite') | avertissements : $((@($win.FindAll($TS::Descendants, $All)) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -match 'sera fermé|OneDrive|ancienne génération' } | Select-Object -Unique) -join ' / ')")

    Log ("Avant lancement : " + (Counts))
    $wvBefore = @(Get-Process msedgewebview2 -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid } | ForEach-Object { $_.Id })
    Invoke-El (Find-Id $win 'FooterStartRestoreButton')
    $dlg = Find-Window $win 'BrowserCloseDialog'
    if (-not $dlg) { Log 'ERREUR : modale de fermeture des navigateurs introuvable' }
    else {
        $items = @($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'DialogItem'))) | ForEach-Object { $_.Current.Name })
        Log ("Modale : " + (Val $dlg 'DialogHeading') + " | " + (Val $dlg 'DialogIntro') + " | " + ($items -join ', '))
        Log ("Avertissement : " + ((Val $dlg 'DialogWarning') -replace "`r?`n", ' / ') + " | boutons : " + (Val $dlg 'DialogSecondary') + " / " + (Val $dlg 'DialogPrimary'))
        Start-Sleep -Milliseconds 500
        Save-Shot $dlg (Join-Path $Shots '12-modale-fermeture-navigateurs.png') | Out-Null
        Invoke-El (Find-Id $dlg 'DialogPrimary')
        $deadline = (Get-Date).AddSeconds(30)
        do { Start-Sleep -Milliseconds 400 } while (((CountP 'chrome') + (CountP 'msedge') + (CountP 'firefox')) -gt 0 -and (Get-Date) -lt $deadline)
        Log ("Après confirmation : " + (Counts))
        $alive = @($wvBefore | Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue })
        Log ("msedgewebview2 : $($alive.Count) / $($wvBefore.Count) processus d'avant toujours actifs (mêmes PID)")
    }
    $deadline = (Get-Date).AddSeconds(60)
    do { Start-Sleep -Milliseconds 300; $pct = [int]('0' + ((Val $win 'RestorePercent' 1) -replace '[^\d]', '')) } while ($pct -lt 15 -and (Get-Date) -lt $deadline)
    Log ("En cours : " + (Val $win 'RestoreCategory') + " | " + (Val $win 'RestoreFiles') + " | " + (Val $win 'RestoreBytes') + " | " + (Val $win 'RestorePercent'))
    Shot $win '13-restauration-en-cours.png'
    $result = Wait-RestoreResult $win 30
    Start-Sleep -Seconds 1
    Log "Résultat : $result"
    Log ("Chiffres : restaurés $(Val $win 'RestoreResultFiles') | volume $(Val $win 'RestoreResultBytes') | remplacés $(Val $win 'RestoreResultReplaced') | erreurs $(Val $win 'RestoreResultErrors') | durée $(Val $win 'RestoreResultDuration')")
    Log ("Navigateurs : " + (Result-Lines $win 'RestoreResultBrowsers'))
    Log ("Wi-Fi : " + (Result-Lines $win 'RestoreResultWifi') + " | Windows : " + (Result-Lines $win 'RestoreResultWindows'))
    $logB = (Val $win 'RestoreLogPath') -replace '^Journal : ', ''
    Log "Journal : $logB"
    Shot $win '14b-restauration-terminee-navigateurs.png'

    $chrome = "$targetB\Local\Google\Chrome\User Data\Default"
    $edge = "$targetB\Local\Microsoft\Edge\User Data\Default"
    Log ("Chrome restauré : Bookmarks $(Test-Path "$chrome\Bookmarks") | Preferences $(Test-Path "$chrome\Preferences") | Login Data $(Test-Path "$chrome\Login Data") | Cookies $(Test-Path "$chrome\Network\Cookies") | Local State $(Test-Path "$targetB\Local\Google\Chrome\User Data\Local State")")
    Log ("Edge restauré : Bookmarks $(Test-Path "$edge\Bookmarks") | Login Data $(Test-Path "$edge\Login Data") | Web Data $(Test-Path "$edge\Web Data")")
    $ini = "$targetB\Roaming\Mozilla\Firefox\profiles.ini"
    if (Test-Path $ini) { Log ("Firefox profiles.ini : " + ((Get-Content $ini) -join ' / ')) }
    Log ("Bureau restauré dans l'environnement de test : " + @(Get-ChildItem -Recurse -File "$tb\Bureau").Count + " fichier(s)")
    Log ("Navigateurs réels : profil Chrome réel non modifié (Local State réel inchangé : " + ((Get-Item "$env:LOCALAPPDATA\Google\Chrome\User Data\Local State").LastWriteTime -lt (Get-Date).AddMinutes(-1)) + ")")
    (Get-Process DataBridge -ErrorAction SilentlyContinue) | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(5000) | Out-Null }
}

$report | Set-Content -Encoding UTF8 (Join-Path $Shots "rapport-$Part.txt")

