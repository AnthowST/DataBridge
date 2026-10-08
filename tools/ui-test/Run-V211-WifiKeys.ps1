# DataBridge 2.1.1 — Wi-Fi keys recovered as administrator, on the real window (Windows PowerShell 5.1, NOT elevated, DEBUG build).
#   - DATABRIDGE_TEST_WIFI_KEYS_BLOCKED=1 shows this PC's readable keys as protected, so the recovery banner appears;
#   - DATABRIDGE_TEST_WIFI_NO_UAC=1 starts the "--wifi-key-worker" instance WITHOUT the UAC prompt (a UAC prompt cannot be
#     automated): same process, same pipe and same protocol as the real elevated worker;
#   - backup with test Known Folders and Wi-Fi only; the keys are never written to the report. The exported XML holds
#     this PC's real key: the whole test folder is deleted at the end (unless -Keep).
param(
    [string]$Exe = "$PSScriptRoot\..\..\src\DataBridge.App\bin\Debug\net10.0-windows\DataBridge.exe",
    [string]$Root = 'D:\ProfileCopy-Tests\V211-Wifi',
    [string]$Shots = "$env:TEMP\DataBridge-V211-Wifi",
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
$CheckCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
New-Item -ItemType Directory -Force $Shots | Out-Null
function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Val($root, [string]$id, [int]$t = 3) { try { (Find-Id $root $id $t).Current.Name } catch { '(absent)' } }
function Shot($win, [string]$name) { Start-Sleep -Milliseconds 600; Save-Shot $win (Join-Path $Shots $name) | Out-Null }
function Wait-Value($win, [string]$id, [scriptblock]$ok, [int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    do { $v = Val $win $id 1; if (& $ok $v) { return $v }; Start-Sleep -Milliseconds 500 } while ((Get-Date) -lt $deadline)
    return $v
}
function Uncheck-All($win) { foreach ($cb in @($win.FindAll($TS::Descendants, $CheckCond))) { if ((Get-ToggleState $cb) -eq 'On') { Toggle-El $cb; Start-Sleep -Milliseconds 250 } } }
function KeyStatuses($win) { (@($win.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'WifiKeyStatus')))) | ForEach-Object { $_.Current.Name }) -join ' ; ' }

if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
$src = "$Root\Profil"
$kf = @{ DESKTOP = "$src\Bureau"; DOCUMENTS = "$src\Documents"; DOWNLOADS = "$src\Telechargements"; PICTURES = "$src\Images"; VIDEOS = "$src\Videos"; MUSIC = "$src\Musique" }
foreach ($d in $kf.Values) { New-Item -ItemType Directory -Force $d | Out-Null }
Set-Content -Encoding UTF8 "$src\Documents\notes.txt" 'notes de test'
foreach ($k in $kf.Keys) { Set-Item "Env:DATABRIDGE_TEST_$k" $kf[$k] }
Set-Item Env:DATABRIDGE_TEST_ROAMING "$Root\AppData\Roaming"
Set-Item Env:DATABRIDGE_TEST_LOCAL "$Root\AppData\Local"
Set-Item Env:DATABRIDGE_TEST_NO_WALLPAPER_APPLY '1'
Set-Item Env:DATABRIDGE_TEST_NO_OUTLOOK '1'
Set-Item Env:DATABRIDGE_TEST_WIFI_KEYS_BLOCKED '1'
Set-Item Env:DATABRIDGE_TEST_WIFI_NO_UAC '1'

Get-Process DataBridge -ErrorAction SilentlyContinue | Stop-Process -Force
$win = Start-App (Resolve-Path $Exe).Path
Start-Sleep -Seconds 2
$proc = Get-Process DataBridge | Select-Object -First 1
Log "Exe : $($proc.Path) | version $($proc.MainModule.FileVersionInfo.ProductVersion) | jeton : $([UiNative]::TokenInfo($proc.Id))"

Invoke-El (Find-Id $win 'ModeBackupButton'); Start-Sleep -Seconds 3
New-Item -ItemType Directory -Force "$Root\Sauvegardes" | Out-Null
Set-Destination $win "$Root\Sauvegardes"
Go-Page $win 'Nav_Browsers'; Start-Sleep -Seconds 1; Uncheck-All $win
Go-Page $win 'Nav_Wallpaper'; Start-Sleep -Seconds 1; Uncheck-All $win
Go-Page $win 'Nav_Printers'; Start-Sleep -Seconds 4; Uncheck-All $win

Go-Page $win 'Nav_Wifi'
$null = Wait-Value $win 'WifiRecoverTitle' { param($x) $x -and $x -ne '(absent)' } 30
Log "Avant : titre « $(Val $win 'WifiRecoverTitle') » | clés : $(KeyStatuses $win)"
Shot $win '01-wifi-cles-protegees.png'

$before = @(Get-Process DataBridge).Count
Invoke-El (Find-Id $win 'RecoverWifiKeysButton')
$msg = Wait-Value $win 'WifiRecoveryMessage' { param($x) $x -and $x -ne '(absent)' -and $x -notmatch '^Demande des droits' } 60
Log "Après : titre « $(Val $win 'WifiRecoverTitle') » | message : $msg"
Log "Clés : $(KeyStatuses $win) | bouton présent : $((Val $win 'RecoverWifiKeysButton' 1) -ne '(absent)') | processus DataBridge restants : $(@(Get-Process DataBridge).Count) (avant : $before)"
Shot $win '02-wifi-cles-recuperees.png'

Go-Page $win 'Nav_Summary'
$null = Wait-Value $win 'SummaryEstimated' { param($x) $x -and $x -notmatch 'calcul|Calcul' } 120
Start-Sleep -Seconds 1
Shot $win '03-resume.png'
Invoke-El (Find-Id $win 'FooterStartCopyButton')
$deadline = (Get-Date).AddMinutes(5); $title = $null
while (-not $title -and (Get-Date) -lt $deadline) { try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }; Start-Sleep -Seconds 1 }
Log "Sauvegarde : $title"
Shot $win '04-resultat.png'

$backup = Get-ChildItem "$Root\Sauvegardes" -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$log = Get-Content -Raw -Encoding UTF8 (Join-Path $backup.FullName 'copy-log.txt')
Log ("copy-log : " + (($log -split "`r?`n") | Where-Object { $_ -match 'Profils Wi-Fi exportés|profil exporté' }) -join ' | ')
foreach ($xml in Get-ChildItem (Join-Path $backup.FullName 'Network\WiFi') -Filter *.xml) {
    $doc = [xml](Get-Content -Raw -Encoding UTF8 $xml.FullName)
    $sk = $doc.WLANProfile.MSM.security.sharedKey
    $material = if ($sk) { [string]$sk.keyMaterial } else { '' }
    $inLog = $material.Length -gt 0 -and $log.Contains($material)
    Log "$($xml.Name) : protected=$($sk.protected) | clé présente : $($material.Length -gt 0) | clé dans copy-log : $inLog"
}
try { Invoke-El (Find-Id $win 'FinishButton' 5) } catch { }
Start-Sleep -Seconds 1
Get-Process DataBridge -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($k in @('WIFI_KEYS_BLOCKED', 'WIFI_NO_UAC', 'NO_OUTLOOK')) { Remove-Item "Env:DATABRIDGE_TEST_$k" -ErrorAction SilentlyContinue }
if (-not $Keep) { Remove-Item -Recurse -Force $Root; Log "Dossier de test supprimé (il contenait la clé Wi-Fi réelle) : $Root" }
Log "Captures : $Shots"
