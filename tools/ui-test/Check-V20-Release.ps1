# DataBridge 2.0 — Release checks (Windows PowerShell 5.1, NOT elevated): rights, UAC, home screen, backup mode,
# reading a 2.0 backup (manifest) and a 1.3 backup (legacy) up to the summary analysis. Nothing is restored.
param(
    [string]$Exe = "$PSScriptRoot\..\..\publish\DataBridge.exe",
    [string]$Manifest = '',
    [string]$Legacy = 'D:\ProfileCopy-Tests\Backups\DataBridge_PC-ANTHOW_Anthow_2026-10-06_17-11',
    [string]$Old = 'D:\ProfileCopy-Tests\Backups\ProfileCopy_PC-ANTHOW_Anthow_2026-10-05_23-54'
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
function Val($root, [string]$id, [int]$t = 3) { try { (Find-Id $root $id $t).Current.Name } catch { '(absent)' } }
foreach ($k in 'DESKTOP', 'DOCUMENTS', 'DOWNLOADS', 'PICTURES', 'VIDEOS', 'MUSIC', 'ROAMING', 'LOCAL', 'NO_WALLPAPER_APPLY') { Remove-Item "Env:DATABRIDGE_TEST_$k" -ErrorAction SilentlyContinue }
if (-not $Manifest) { $Manifest = (Get-ChildItem 'D:\ProfileCopy-Tests\E2E-20\A\Sauvegardes' -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName }

$consent = $false
$win = Start-App (Resolve-Path $Exe).Path
for ($i = 0; $i -lt 8; $i++) { if (Get-Process consent -ErrorAction SilentlyContinue) { $consent = $true }; Start-Sleep -Milliseconds 250 }
$proc = Get-Process DataBridge | Select-Object -First 1
"Exe : $($proc.Path) | version $($proc.MainModule.FileVersionInfo.ProductVersion)"
"Jeton : $([UiNative]::TokenInfo($proc.Id)) | invite UAC vue : $consent"
"Titre au démarrage : $($win.Current.Name) | écran : $(Val $win 'ModeChoiceTitle') | $(Val $win 'VersionText')"

Invoke-El (Find-Id $win 'ModeBackupButton'); Start-Sleep -Seconds 3
"Mode sauvegarde : $($win.Current.Name) | $(Val $win 'StepsTitle') | pied : $(Val $win 'FooterSelection' 10)"
Invoke-El (Find-Id $win 'HomeButton'); Start-Sleep -Seconds 1
Invoke-El (Find-Id $win 'ModeRestoreButton'); Start-Sleep -Seconds 2
"Mode restauration : $($win.Current.Name) | $(Val $win 'StepsTitle')"

foreach ($b in $Manifest, $Legacy, $Old) {
    Invoke-El (Find-Id $win 'ChooseBackupButton')
    Complete-FileDialog $b | Out-Null
    $deadline = (Get-Date).AddSeconds(40)
    do { Start-Sleep -Milliseconds 500; $pc = Val $win 'BackupComputer' 1 } while (($pc -eq '(absent)' -or $pc -eq 'inconnu' -or -not $pc) -and (Get-Date) -lt $deadline)
    Start-Sleep -Seconds 2
    $gen = @($win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like 'Sauvegarde DataBridge (*' -or $_ -like 'Sauvegarde DataBridge ancienne*' } | Select-Object -Unique) -join ' / '
    "{0}`n   -> {1} | PC {2} | session {3} | {4} | {5} | données {6} | fichiers {7}" -f $b, $gen, $pc, (Val $win 'BackupUser'), (Val $win 'BackupDate'), (Val $win 'BackupVersion'), (Val $win 'BackupSize'), (Val $win 'BackupFiles')
}
# Summary analysis on the 1.3 backup (read-only: compares with this PC, writes nothing).
Invoke-El (Find-Id $win 'ChooseBackupButton'); Complete-FileDialog $Legacy | Out-Null; Start-Sleep -Seconds 4
Invoke-El (Find-Id $win 'UseBackupButton'); Start-Sleep -Seconds 2
Go-Page $win 'R_Nav_Summary'
$deadline = (Get-Date).AddSeconds(180)
do { Start-Sleep -Milliseconds 500; $v = Val $win 'RestoreFilesToWrite' 1 } while ($v -notmatch '^\d' -and (Get-Date) -lt $deadline)
"Analyse 1.3 sur ce PC (rien n'est écrit) : à restaurer $(Val $win 'RestoreVolume') | fichiers $v | remplacés $(Val $win 'RestoreReplaced') | existants $(Val $win 'RestoreExisting')"
$proc.CloseMainWindow() | Out-Null
