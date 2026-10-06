# V1.2 scenario on the real window (Windows PowerShell 5.1, NOT elevated): opens Chrome + Edge + Firefox,
# selects them, confirms the browser-close dialog, force-closes them (session only), then copies and verifies.
param(
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots\v1.2",
    [string]$Destination = 'D:\ProfileCopy-Tests\Backups'
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
New-Item -ItemType Directory -Force $Shots | Out-Null
function Log($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Val($root, [string]$id, [int]$t = 3) { try { (Find-Id $root $id $t).Current.Name } catch { '(absent)' } }
function Texts($el) { ($el.FindAll($TS::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 1 }) -join ' | ' }

$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
$firefox = 'C:\Program Files\Mozilla Firefox\firefox.exe'
$sid = (Get-Process -Id $PID).SessionId
function CountBrowser([string]$name) { @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid }).Count }
function BrowserCounts { "chrome=$(CountBrowser 'chrome') msedge=$(CountBrowser 'msedge') firefox=$(CountBrowser 'firefox')" }
function Find-Dialog([int]$TimeoutSec = 25) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'BrowserCloseDialog')
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $d = $AE::RootElement.FindFirst($TS::Descendants, $cond)
        if ($d) { return $d }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

# Open the three browsers and let them spawn their child processes.
Log "Ouverture des navigateurs…"
Start-Process $chrome 'about:blank' | Out-Null
Start-Process $edge 'about:blank' | Out-Null
Start-Process $firefox 'about:blank' | Out-Null
Start-Sleep -Seconds 8
Log ("Processus navigateurs (session $sid) : " + (BrowserCounts))

# Launch the app without elevation.
$consent = $false
$win = Start-App
for ($i = 0; $i -lt 8; $i++) { if (Get-Process consent -ErrorAction SilentlyContinue) { $consent = $true }; Start-Sleep -Milliseconds 250 }
$proc = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton : $([UiNative]::TokenInfo($proc.Id))  | UAC vu : $consent"

Set-Destination $win $Destination
Log ("Destination : " + (Val $win 'BackupFolderPreview'))

# Browsers page: all three detected and running, profiles preselected.
Go-Page $win 'Nav_Browsers'
Start-Sleep -Seconds 2
Save-Shot $win "$Shots\02-navigation-nouvelle-da.png" | Out-Null

# Personal page for the centred-checkbox captures (full + partial).
Go-Page $win 'Nav_Personal'
$deadline = (Get-Date).AddSeconds(120)
while ((Val $win 'FooterSelection') -match 'calcul' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 1
Save-Shot $win "$Shots\03-checkbox-centrees.png" | Out-Null
Expand-Row $win 'Documents' | Out-Null
Expand-Row $win 'Virtual Machines' | Out-Null
Expand-Row $win 'WS2025' | Out-Null
$bigName = '564d34a0-f4da-8cca-5762-0feaf5e6af75.vmem'
try { Toggle-El (Get-RowCheck $win $bigName); Start-Sleep -Seconds 2 } catch { Log "vmem introuvable: $_" }
Find-Row $win 'Documents' | Out-Null
Start-Sleep -Milliseconds 600
Save-Shot $win "$Shots\04-etat-partiel-centre.png" | Out-Null

# Summary.
Go-Page $win 'Nav_Summary'
Start-Sleep -Seconds 2
Save-Shot $win "$Shots\05-resume-charte.png" | Out-Null

# Launch -> the browser-close confirmation dialog must appear.
Log ("Avant lancement : " + (BrowserCounts))
Invoke-El (Find-Id $win 'FooterStartCopyButton')
$dlg = Find-Dialog
if (-not $dlg) { Log 'ERREUR: fenetre de confirmation navigateurs introuvable'; return }
Log ("Dialogue : " + (Val $dlg 'BrowserCloseHeading'))
$items = @($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'BrowserCloseItem'))) | ForEach-Object { $_.Current.Name })
Log ("Navigateurs listés : " + ($items -join ', '))
Save-Shot $dlg "$Shots\06-confirmation-navigateurs.png" | Out-Null

# Confirm: force-close.
Invoke-El (Find-Id $dlg 'BrowserCloseConfirm')

# Wait until no selected browser process remains in the session (a few seconds).
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $c = (CountBrowser 'chrome') + (CountBrowser 'msedge') + (CountBrowser 'firefox') } while ($c -gt 0 -and (Get-Date) -lt $deadline)
Log ("Après fermeture : " + (BrowserCounts))

# Copy in progress (blue bar) then result.
$deadline = (Get-Date).AddSeconds(60)
do { Start-Sleep -Milliseconds 400; $f = Val $win 'CopyFiles' 1 } while (($f -notmatch '/') -and (Get-Date) -lt $deadline)
Start-Sleep -Milliseconds 800
Log ("Copie : " + (Val $win 'CopyCategory') + " | " + (Val $win 'CopyFiles') + " | " + (Val $win 'CopyPercent'))
Save-Shot $win "$Shots\07-copie-en-cours.png" | Out-Null

$deadline = (Get-Date).AddMinutes(25)
$title = $null
while (-not $title -and (Get-Date) -lt $deadline) {
    try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 1
Log "Résultat : $title"
Log ("Données : " + (Texts (Find-Id $win 'ResultLines')))
Log ("Contrôle : " + (Val $win 'ResultControl'))
Save-Shot $win "$Shots\08-resultat-final.png" | Out-Null
Log ("Processus navigateurs restants (session) : " + (BrowserCounts))
$p2 = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton en fin de test : $([UiNative]::TokenInfo($p2.Id))"
