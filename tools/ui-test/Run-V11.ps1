# V1.1 scenario on the real window (Windows PowerShell 5.1, NOT elevated): separate counters, local/cloud display,
# recursive destination blocked, destination refused as additional source, real copy with cloud files left online.
param(
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots\v1.1",
    [string]$Destination = 'D:\ProfileCopy-Tests\Backups',
    [string]$Sources = 'D:\ProfileCopy-Tests\Sources'
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
New-Item -ItemType Directory -Force $Shots | Out-Null
function Log($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Texts($el) { ($el.FindAll($TS::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 1 }) -join ' | ' }
function Val([string]$id, [int]$t = 3) { try { (Find-Id $win $id $t).Current.Name } catch { '(absent)' } }
function Present([string]$id) { try { $e = Find-Id $win $id 1; return -not $e.Current.IsOffscreen } catch { return $false } }

# Launch without elevation
$consentSeen = $false
$win = Start-App
for ($i = 0; $i -lt 8; $i++) { if (Get-Process consent -ErrorAction SilentlyContinue) { $consentSeen = $true }; Start-Sleep -Milliseconds 250 }
$proc = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton : $([UiNative]::TokenInfo($proc.Id))  (elevated=0 / 0x2000 = Medium = non administrateur)"
Log "consent.exe (UAC) observé : $consentSeen"

Set-Destination $win $Destination
Log ("Destination : " + (Val 'BackupFolderPreview') + " -> " + (Val 'DestinationCheck'))

# 1. Personal folders: local / online-only
Go-Page $win 'Nav_Personal'
$deadline = (Get-Date).AddSeconds(120)
while ((Val 'FooterSelection') -match 'calcul' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
$locals = @(Find-All $win 'RootLocal' | ForEach-Object { $_.Current.Name })
$details = @(Find-All $win 'RootDetail' | ForEach-Object { $_.Current.Name })
$clouds = @(Find-All $win 'RootCloud' | ForEach-Object { $_.Current.Name })
for ($i = 0; $i -lt $locals.Count; $i++) { Log ("Dossier : " + $locals[$i] + " | " + $details[$i]) }
foreach ($c in $clouds) { Log "En ligne : $c" }
Save-Shot $win "$Shots\01-dossiers-personnels-local-cloud.png" | Out-Null

# Same selection as V1: uncheck the 4 Go virtual machine memory file
Expand-Row $win 'Documents' | Out-Null
Expand-Row $win 'Virtual Machines' | Out-Null
Expand-Row $win 'WS2025' | Out-Null
$bigName = '564d34a0-f4da-8cca-5762-0feaf5e6af75.vmem'
Toggle-El (Get-RowCheck $win $bigName)
Start-Sleep -Seconds 2
Log ("Gros fichier décoché : " + (Get-ToggleState (Get-RowCheck $win $bigName)) + " | " + (Val 'FooterSelection'))

# Manual additions (same as V1) + the destination itself refused as a source
Go-Page $win 'Nav_Additional'
Invoke-El (Find-Id $win 'AddFolderButton')
Complete-FileDialog "$Sources\Comptabilité" | Out-Null
Start-Sleep -Seconds 1
Invoke-El (Find-Id $win 'AddFilesButton')
Complete-FileDialog "`"$Sources\config.xml`" `"$Sources\notes-poste.txt`"" | Out-Null
Start-Sleep -Seconds 1
Invoke-El (Find-Id $win 'AddFilesButton')
Complete-FileDialog "$env:USERPROFILE\Documents\Wiki Masters  1.19.txt" | Out-Null
Start-Sleep -Seconds 1
Invoke-El (Find-Id $win 'AddFolderButton')
Complete-FileDialog $Destination | Out-Null
Start-Sleep -Seconds 2
Log ("Ajout de la destination comme source : " + (Val 'AdditionalMessage'))
foreach ($s in (Find-All $win 'AdditionalStatus')) { Log ("Statut : " + $s.Current.Name) }
Save-Shot $win "$Shots\06-destination-refusee-comme-source.png" | Out-Null

# 2. Summary before the copy
Go-Page $win 'Nav_Summary'
Start-Sleep -Seconds 3
foreach ($id in 'SummaryEstimated', 'SummaryPlannedFiles', 'SummarySelectedFiles', 'SummaryPlannedCount', 'SummaryCloudFiles', 'SummaryCloudFate', 'SummaryFree', 'FooterSelection') {
    Log ("$id : " + (Val $id))
}
Save-Shot $win "$Shots\02-resume-avant-copie.png" | Out-Null

# 3. Recursive destination: inside Documents, which is selected
Set-Destination $win "$env:USERPROFILE\Documents"
Start-Sleep -Seconds 3
Log ("Destination Documents (page Destination) : " + (Val 'DestinationCheck'))
Save-Shot $win "$Shots\03b-destination-dans-documents.png" | Out-Null
Go-Page $win 'Nav_Summary'
$deadline = (Get-Date).AddSeconds(30)
while (-not (Present 'DestinationConflictTitle') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 1
Log ("Blocage : " + (Val 'DestinationConflictTitle') + " | " + (Val 'DestinationConflictDetail') + " | " + (Val 'BlockReason'))
Log ("Bouton Lancer la copie actif : " + (Find-Id $win 'FooterStartCopyButton').Current.IsEnabled)
Save-Shot $win "$Shots\03-destination-recursive-bloquee.png" | Out-Null

# Back to the real destination
Set-Destination $win $Destination
Go-Page $win 'Nav_Summary'
$deadline = (Get-Date).AddSeconds(30)
while ((Present 'DestinationConflictTitle') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
Log ("Après retour sur $Destination : blocage affiché = " + (Present 'DestinationConflictTitle') + " | Lancer actif = " + (Find-Id $win 'FooterStartCopyButton').Current.IsEnabled)
foreach ($id in 'SummaryEstimated', 'SummarySelectedFiles', 'SummaryPlannedCount', 'SummaryCloudFiles', 'SummaryCloudFate') { Log ("$id : " + (Val $id)) }

# 4. Real copy (cloud-only files left online)
Invoke-El (Find-Id $win 'FooterStartCopyButton')
$deadline = (Get-Date).AddSeconds(120)
do { Start-Sleep -Milliseconds 400; $files = Val 'CopyFiles' 1 } while (($files -notmatch '/') -and (Get-Date) -lt $deadline)
$deadline = (Get-Date).AddSeconds(90)
do { Start-Sleep -Milliseconds 400; $pct = [int](((Val 'CopyPercent' 1) -replace '[^\d]', '') + '0') / 10 } while ($pct -lt 35 -and (Get-Date) -lt $deadline)
Log ("Copie : " + (Val 'CopyCategory') + " | " + (Val 'CopyFiles') + " | " + (Val 'CopyBytes') + " | " + (Val 'CopyPercent'))
Log ("Copie : " + (Val 'CopySelection') + " | cloud " + (Val 'CopyCloud') + " | erreurs " + (Val 'CopyErrors') + " | ignorés " + (Val 'CopyIgnored'))
Save-Shot $win "$Shots\04-copie-en-cours.png" | Out-Null

$deadline = (Get-Date).AddMinutes(25)
$title = $null
while (-not $title -and (Get-Date) -lt $deadline) {
    try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 1

# 5. Final result
Log "Résultat : $title"
Log ("Traitement : " + (Val 'ResultProcessed'))
Log ("Contrôle : " + (Val 'ResultControl'))
Log ("Données : " + (Texts (Find-Id $win 'ResultLines')))
Log ("Wi-Fi : " + (Texts (Find-Id $win 'ResultWifi')))
Log ("Durée : " + (Val 'ResultDuration'))
Save-Shot $win "$Shots\05-resultat-final.png" | Out-Null
$p2 = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton en fin de test : $([UiNative]::TokenInfo($p2.Id))"
