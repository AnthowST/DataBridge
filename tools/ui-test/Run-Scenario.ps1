# Full UI scenario on the real window (Windows PowerShell 5.1, NOT elevated).
# Prerequisite: D:\ProfileCopy-Tests\Sources (test data) and D:\ProfileCopy-Tests\Backups exist.
param(
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots",
    [string]$Destination = 'D:\ProfileCopy-Tests\Backups',
    [string]$Sources = 'D:\ProfileCopy-Tests\Sources',
    [switch]$SkipCopy
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
New-Item -ItemType Directory -Force $Shots | Out-Null
function Log($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Texts($el) { ($el.FindAll($TS::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 1 }) -join ' | ' }
function View($win, $cls) { $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty, $cls))) }

# 1-2. Launch without elevation, no UAC prompt
$consentSeen = $false
$win = Start-App
for ($i = 0; $i -lt 8; $i++) { if (Get-Process consent -ErrorAction SilentlyContinue) { $consentSeen = $true }; Start-Sleep -Milliseconds 250 }
$proc = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton : $([UiNative]::TokenInfo($proc.Id))  (elevated=0 / 0x2000 = Medium = non administrateur)"
Log "consent.exe (UAC) observé : $consentSeen"
# 3. User detection
Log ("En-tête : " + (Find-Id $win 'HeaderPc').Current.Name + " | " + (Find-Id $win 'HeaderSession').Current.Name + " | " + (Find-Id $win 'HeaderProfile').Current.Name)
Log ("Droits affichés : " + (Find-Id $win 'HomeElevation').Current.Name)
Start-Sleep -Seconds 2
Save-Shot $win "$Shots\01-accueil.png" | Out-Null

# 6. Destination through the native folder picker
Set-Destination $win $Destination
Log ("Destination : " + (Find-Id $win 'BackupFolderPreview').Current.Name + " -> " + (Find-Id $win 'DestinationCheck').Current.Name)
Save-Shot $win "$Shots\02-destination.png" | Out-Null

# 4-5. Known folders / OneDrive
Go-Page $win 'Nav_Personal'
$deadline = (Get-Date).AddSeconds(60)
while ((Find-Id $win 'FooterSelection').Current.Name -match 'calcul' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 1
$list = Find-Id $win 'ExplorerList'
foreach ($row in $list.FindAll($TS::Children, $All)) { Log ("Dossier : " + (Texts $row)) }
Save-Shot $win "$Shots\03-dossiers-personnels.png" | Out-Null
$footerBefore = (Find-Id $win 'FooterSelection').Current.Name

# 7. Navigation in Documents
Expand-Row $win 'Documents' | Out-Null
Expand-Row $win 'Virtual Machines' | Out-Null
Expand-Row $win 'WS2025' | Out-Null
($list.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)).SetScrollPercent(-1, 100)
Start-Sleep -Milliseconds 500
Find-Row $win 'Virtual Machines' | Out-Null
Start-Sleep -Milliseconds 600
Save-Shot $win "$Shots\04-explorateur-dossier-ouvert.png" | Out-Null

# 8-9. Uncheck the big file, size recalculated
$bigName = '564d34a0-f4da-8cca-5762-0feaf5e6af75.vmem'
Toggle-El (Get-RowCheck $win $bigName)
Start-Sleep -Seconds 2
Log ("Gros fichier : " + (Get-ToggleState (Get-RowCheck $win $bigName)) + " | WS2025 : " + (Get-ToggleState (Get-RowCheck $win 'WS2025')))
$footerAfter = (Find-Id $win 'FooterSelection').Current.Name
Log "Sélection avant : $footerBefore"
Log "Sélection après : $footerAfter"
Find-Row $win 'Virtual Machines' | Out-Null
Start-Sleep -Milliseconds 500
Save-Shot $win "$Shots\05-gros-fichier-decoche.png" | Out-Null

# Search helper (does not change the selection)
Set-Text (Find-Id $win 'SearchBox') '.iso'
Start-Sleep -Seconds 3
Log ("Recherche .iso : " + (((Find-Id $win 'SearchList').FindAll($TS::Children, $All) | ForEach-Object { $_.Current.Name }) -join ', '))
Set-Text (Find-Id $win 'SearchBox') ''
Toggle-El (Find-Id $win 'LargeOnlyToggle')
Start-Sleep -Seconds 4
Save-Shot $win "$Shots\04b-recherche-gros-fichiers.png" | Out-Null
Log ("Sélection inchangée par la recherche : " + ($footerAfter -eq (Find-Id $win 'FooterSelection').Current.Name))
Invoke-El (Find-Id $win 'ClearSearchButton')
Start-Sleep -Milliseconds 800

# 10-12. Manual additions + anti-duplicates
Go-Page $win 'Nav_Additional'
(Find-Id $win 'AddFolderButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Complete-FileDialog "$Sources\Comptabilité" | Out-Null
Start-Sleep -Seconds 1
(Find-Id $win 'AddFilesButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Complete-FileDialog "`"$Sources\config.xml`" `"$Sources\notes-poste.txt`"" | Out-Null
Start-Sleep -Seconds 1
Log ("Message ajout : " + (Find-Id $win 'AdditionalMessage').Current.Name)
(Find-Id $win 'AddFilesButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Complete-FileDialog "$env:USERPROFILE\Documents\Wiki Masters  1.19.txt" | Out-Null
Start-Sleep -Seconds 2
foreach ($s in (Find-All $win 'AdditionalStatus')) { Log ("Statut : " + $s.Current.Name) }
Save-Shot $win "$Shots\06-ajout-manuel.png" | Out-Null

# 13. Outlook signatures
Go-Page $win 'Nav_Outlook'
Log ("Signatures : " + (Find-Id $win 'SignatureCount').Current.Name)
Save-Shot $win "$Shots\06b-signatures-outlook.png" | Out-Null

# 14-16. Browsers
Go-Page $win 'Nav_Browsers'
Start-Sleep -Seconds 1
Log ("Navigateurs : " + (Texts (View $win 'BrowsersView')))
Save-Shot $win "$Shots\07-navigateurs.png" | Out-Null

# 17. Wi-Fi
Go-Page $win 'Nav_Wifi'
Start-Sleep -Seconds 1
Log ("Wi-Fi : " + (Texts (View $win 'WifiView')))
Save-Shot $win "$Shots\08-wifi.png" | Out-Null

# 19. Wallpaper
Go-Page $win 'Nav_Wallpaper'
Start-Sleep -Seconds 1
Log ("Fond d'écran : " + (Find-Id $win 'WallpaperDetail').Current.Name)
Save-Shot $win "$Shots\08b-fond-ecran.png" | Out-Null

# Summary
Go-Page $win 'Nav_Summary'
Start-Sleep -Seconds 2
Log ("Résumé : estimé " + (Find-Id $win 'SummaryEstimated').Current.Name + " / libre " + (Find-Id $win 'SummaryFree').Current.Name)
Save-Shot $win "$Shots\09-resume.png" | Out-Null
if ($SkipCopy) { return }

# 20-21. Real copy + progress
Invoke-El (Find-Id $win 'FooterStartCopyButton')
Start-Sleep -Milliseconds 2500
Log ("Copie : " + (Find-Id $win 'CopyCategory').Current.Name + " | " + (Find-Id $win 'CopyFiles').Current.Name + " | " + (Find-Id $win 'CopyBytes').Current.Name + " | " + (Find-Id $win 'CopyPercent').Current.Name)
Save-Shot $win "$Shots\10-copie-en-cours.png" | Out-Null
$deadline = (Get-Date).AddMinutes(20)
$title = $null
while (-not $title -and (Get-Date) -lt $deadline) {
    try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 1
# 24. Final result
Log "Résultat : $title"
Log ("Données : " + (Texts (Find-Id $win 'ResultLines')))
Log ("Wi-Fi : " + (Texts (Find-Id $win 'ResultWifi')))
Log ("Durée : " + (Find-Id $win 'ResultDuration').Current.Name)
Save-Shot $win "$Shots\11-resultat-final.png" | Out-Null
