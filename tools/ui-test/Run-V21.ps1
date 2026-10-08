# DataBridge 2.1 — Outlook mailbox and printers on the real window (Windows PowerShell 5.1, NOT elevated, DEBUG build).
# Nothing touches the real Outlook nor modifies a printer:
#   - backup: test Known Folders (DATABRIDGE_TEST_*), browsers and Wi-Fi unchecked, printers exported (read-only:
#     inventory + driver package export with the session rights), the Outlook page is shown WITHOUT "Analyser Outlook";
#   - restore: the new backup is opened, the printers archive is checked and this PC analysed (read-only), the restore
#     is NOT launched. A placeholder mailbox entry (fake PST, never opened) is added to the manifest only to display
#     the restore Outlook page; "Lister les boîtes Outlook" is never clicked.
param(
    [string]$Exe = "$PSScriptRoot\..\..\src\DataBridge.App\bin\Debug\net10.0-windows\DataBridge.exe",
    [string]$Root = 'D:\ProfileCopy-Tests\V21',
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots\databridge-2.1.0"
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
function Shot($win, [string]$name) { Start-Sleep -Milliseconds 600; Save-Shot $win (Join-Path $Shots $name) | Out-Null }
function Wait-Value($win, [string]$id, [scriptblock]$ok, [int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { $v = Val $win $id 1; if (& $ok $v) { return $v }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
    return $v
}
function Uncheck-All($win) { foreach ($cb in @($win.FindAll($TS::Descendants, $CheckCond))) { if ((Get-ToggleState $cb) -eq 'On') { Toggle-El $cb; Start-Sleep -Milliseconds 250 } } }

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
$src = "$Root\Profil"
$kf = @{ DESKTOP = "$src\Bureau"; DOCUMENTS = "$src\Documents"; DOWNLOADS = "$src\Telechargements"; PICTURES = "$src\Images"; VIDEOS = "$src\Videos"; MUSIC = "$src\Musique" }
foreach ($d in $kf.Values) { New-Item -ItemType Directory -Force $d | Out-Null }
Set-Content -Encoding UTF8 "$src\Documents\notes.txt" 'notes de test'
Set-Content -Encoding UTF8 "$src\Bureau\a-faire.txt" 'à faire'
foreach ($k in @('DESKTOP', 'DOCUMENTS', 'DOWNLOADS', 'PICTURES', 'VIDEOS', 'MUSIC', 'ROAMING', 'LOCAL', 'NO_WALLPAPER_APPLY')) { Remove-Item "Env:DATABRIDGE_TEST_$k" -ErrorAction SilentlyContinue }
foreach ($k in $kf.Keys) { Set-Item "Env:DATABRIDGE_TEST_$k" $kf[$k] }
Set-Item Env:DATABRIDGE_TEST_ROAMING "$Root\AppData\Roaming"
Set-Item Env:DATABRIDGE_TEST_LOCAL "$Root\AppData\Local"
Set-Item Env:DATABRIDGE_TEST_NO_WALLPAPER_APPLY '1'
# 2.1.1: the Outlook pages list the mailboxes automatically; this Debug-only switch keeps the scenario away from Outlook.
Set-Item Env:DATABRIDGE_TEST_NO_OUTLOOK '1'

Get-Process DataBridge -ErrorAction SilentlyContinue | Stop-Process -Force
$win = Start-App (Resolve-Path $Exe).Path
Start-Sleep -Seconds 2
$proc = Get-Process DataBridge | Select-Object -First 1
Log "Exe : $($proc.Path) | version $($proc.MainModule.FileVersionInfo.ProductVersion) | jeton : $([UiNative]::TokenInfo($proc.Id))"

# ===================================================================================== backup
Invoke-El (Find-Id $win 'ModeBackupButton'); Start-Sleep -Seconds 3
New-Item -ItemType Directory -Force "$Root\Sauvegardes" | Out-Null
Set-Destination $win "$Root\Sauvegardes"
Go-Page $win 'Nav_Outlook'; Start-Sleep -Seconds 1
Log ("Page Outlook : " + (Texts (Find-Id $win 'AnalyzeOutlookButton')) + " | bouton présent, NON cliqué (Outlook non touché)")
Shot $win '01-sauvegarde-outlook.png'
Go-Page $win 'Nav_Browsers'; Start-Sleep -Seconds 1; Uncheck-All $win
Go-Page $win 'Nav_Wifi'; Start-Sleep -Seconds 2; Uncheck-All $win
Go-Page $win 'Nav_Wallpaper'; Start-Sleep -Seconds 1; Uncheck-All $win
Go-Page $win 'Nav_Printers'
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and @((Find-Id $win 'PrintersList' 2).FindAll($TS::Descendants, $CheckCond)).Count -eq 0) { Start-Sleep -Milliseconds 500 }
$printerChecks = @((Find-Id $win 'PrintersList' 2).FindAll($TS::Descendants, $CheckCond))
Log ("Imprimantes listées : " + (($printerChecks | ForEach-Object { "$($_.Current.Name)=$(Get-ToggleState $_)" }) -join ' ; '))
Shot $win '02-sauvegarde-imprimantes.png'
Go-Page $win 'Nav_Summary'
$null = Wait-Value $win 'SummaryEstimated' { param($x) $x -and $x -notmatch 'calcul|Calcul' } 120
Start-Sleep -Seconds 2
Shot $win '03-sauvegarde-resume.png'
Invoke-El (Find-Id $win 'FooterStartCopyButton')
$deadline = (Get-Date).AddMinutes(10); $title = $null
while (-not $title -and (Get-Date) -lt $deadline) { try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }; Start-Sleep -Seconds 1 }
Log "Sauvegarde : $title | fichiers $(Val $win 'ResultFiles') | erreurs $(Val $win 'ResultErrors')"
Log ("Résultat Outlook / imprimantes : " + ((@((Find-Id $win 'ResultExtras').FindAll($TS::Descendants, $All)) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 1 }) -join ' | '))
Shot $win '04-sauvegarde-resultat.png'
$backup = Get-ChildItem "$Root\Sauvegardes" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$manifestPath = Join-Path $backup.FullName 'databridge-manifest.json'
$prnbak = Join-Path $backup.FullName 'Imprimantes\Imprimantes.prnbak'
Log ("Archive imprimantes : $(Test-Path $prnbak) ($((Get-Item $prnbak -ErrorAction SilentlyContinue).Length) octets)")
$json = Get-Content -Raw -Encoding UTF8 $manifestPath | ConvertFrom-Json
Log ("Manifest : version $($json.dataBridgeVersion), catégories $($json.categories -join ', '), imprimantes : $(($json.printers.printers | ForEach-Object { $_.name }) -join ', ')")
$log = Get-Content -Raw -Encoding UTF8 (Join-Path $backup.FullName 'copy-log.txt')
Log ("copy-log : section IMPRIMANTES $($log -match 'IMPRIMANTES') | section OUTLOOK $($log -match 'OUTLOOK — BOÎTE AUX LETTRES') | 'Aucune boîte aux lettres sélectionnée' $($log -match 'Aucune boîte aux lettres sélectionnée')")
Invoke-El (Find-Id $win 'FinishButton' 5)
Start-Sleep -Seconds 2

# Placeholder mailbox (display only): fake PST and autocomplete files, never opened.
$boite = Join-Path $backup.FullName 'Outlook\Boite'
New-Item -ItemType Directory -Force $boite | Out-Null
[IO.File]::WriteAllBytes("$boite\Sauvegarde_Outlook_test.pst", (New-Object byte[] 1024))
[IO.File]::WriteAllBytes("$boite\Autocompletion_test.bin", (New-Object byte[] 64))
$raw = Get-Content -Raw -Encoding UTF8 $manifestPath
$mailbox = '"outlookMailbox": { "mailboxName": "boite.test@exemple.ch", "categories": ["mails","calendar","contacts","autocomplete"], "relativePath": "Outlook\\Boite", "pstFile": "Outlook\\Boite\\Sauvegarde_Outlook_test.pst", "autocompleteFile": "Outlook\\Boite\\Autocompletion_test.bin", "mails": 1250, "contacts": 84, "events": 37, "status": "completed" },'
$raw = $raw -replace '^\{', ("{`n  " + $mailbox)
[IO.File]::WriteAllText($manifestPath, $raw, (New-Object Text.UTF8Encoding $false))

# ===================================================================================== restore (no launch)
$win = Get-AppWindow
# "Terminer" already returned to the DataBridge home (the home button is hidden there).
try { Invoke-El (Find-Id $win 'HomeButton' 2) } catch { }
Start-Sleep -Seconds 1
Invoke-El (Find-Id $win 'ModeRestoreButton'); Start-Sleep -Seconds 2
Invoke-El (Find-Id $win 'ChooseBackupButton')
Complete-FileDialog $backup.FullName | Out-Null
$null = Wait-Value $win 'BackupComputer' { param($x) $x -and $x -ne '(absent)' -and $x -ne 'inconnu' } 60
Start-Sleep -Seconds 2
Shot $win '05-restauration-sauvegarde.png'
Invoke-El (Find-Id $win 'UseBackupButton'); Start-Sleep -Seconds 2
Go-Page $win 'R_Nav_Outlook'; Start-Sleep -Seconds 1
Log ("Restauration — Outlook : " + (Val $win 'MailboxContent') + " | cible : " + (Val $win 'OutlookTarget') + " | « Lister les boîtes » NON cliqué")
Shot $win '06-restauration-outlook.png'
Go-Page $win 'R_Nav_Printers'
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and (Val $win 'RestorePrinterState' 1) -eq '(absent)' -and (Val $win 'RestorePrintersMessage' 1) -eq '(absent)') { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 1
$states = @($win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'RestorePrinterState')))) | ForEach-Object { $_.Current.Name }
$checks = @($win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'RestorePrinterCheck')))) | ForEach-Object { "$($_.Current.Name)=$(Get-ToggleState $_) actif=$($_.Current.IsEnabled)" }
Log ("Restauration — imprimantes : " + ($checks -join ' ; ') + " | états : " + ($states -join ' ; ') + " | message : " + (Val $win 'RestorePrintersMessage' 1))
Shot $win '07-restauration-imprimantes.png'
Go-Page $win 'R_Nav_Summary'; Start-Sleep -Seconds 4
Log ("Résumé : " + (Texts (Find-Id $win 'R_Nav_Summary')))
Shot $win '08-restauration-resume.png'
$proc = Get-Process DataBridge | Select-Object -First 1
$proc.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 2
if (-not $proc.HasExited) { $proc | Stop-Process -Force }
Log 'Restauration NON lancée (aucune imprimante créée, Outlook non touché).'
$report | Set-Content -Encoding UTF8 (Join-Path $Shots 'rapport.txt')
