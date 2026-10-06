# DataBridge 1.3 scenario on the real window (Windows PowerShell 5.1, NOT elevated), with the Release exe:
# branding + logo, partial selection, cloud files left online, Chrome + Edge + Firefox opened then force-closed
# after the DataBridge dialog, real copy, counters / log / result, and a 100 % DPI pass on the second monitor.
param(
    [string]$Exe = "$PSScriptRoot\..\..\publish\DataBridge.exe",
    [string]$Shots = "$PSScriptRoot\..\..\docs\screenshots\databridge",
    [string]$Destination = 'D:\ProfileCopy-Tests\Backups',
    [int]$Monitor100X = -1820, [int]$Monitor100Y = 80
)
$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
New-Item -ItemType Directory -Force $Shots | Out-Null
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Shot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  public static void Move(IntPtr h, int x, int y) { SetThreadDpiAwarenessContext(new IntPtr(-4)); SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004); }
  // Crops a screen rectangle (physical pixels) out of the window rendering and enlarges it (nearest neighbour: pixels stay visible).
  public static void Zoom(IntPtr h, int x, int y, int w, int hh, int scale, string path) {
    SetThreadDpiAwarenessContext(new IntPtr(-4));
    RECT wr; GetWindowRect(h, out wr);
    using (var full = new Bitmap(wr.R - wr.L, wr.B - wr.T, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(full)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      var crop = Rectangle.Intersect(new Rectangle(x - wr.L, y - wr.T, w, hh), new Rectangle(0, 0, full.Width, full.Height));
      using (var part = full.Clone(crop, PixelFormat.Format32bppArgb))
      using (var big = new Bitmap(part.Width * scale, part.Height * scale))
      using (var g2 = Graphics.FromImage(big)) {
        g2.InterpolationMode = InterpolationMode.NearestNeighbor; g2.PixelOffsetMode = PixelOffsetMode.Half;
        g2.DrawImage(part, new Rectangle(0, 0, big.Width, big.Height));
        big.Save(path, ImageFormat.Png);
      }
    }
  }
}
'@
function Log($m) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Val($root, [string]$id, [int]$t = 3) { try { (Find-Id $root $id $t).Current.Name } catch { '(absent)' } }
function Texts($el) { ($el.FindAll($TS::Descendants, $All) | ForEach-Object { $_.Current.Name } | Where-Object { $_ -and $_.Trim().Length -gt 1 }) -join ' | ' }
$sid = (Get-Process -Id $PID).SessionId
function CountP([string]$name) { @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid }).Count }
function Counts { "chrome=$(CountP 'chrome') msedge=$(CountP 'msedge') firefox=$(CountP 'firefox') | msedgewebview2=$(CountP 'msedgewebview2')" }
function Find-Window($owner, [string]$id, [int]$TimeoutSec = 25) {
    # An owned WPF dialog is exposed by UI Automation as a child of its owner window (not of the desktop root).
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
# msedgewebview2 processes of the session, with their parent (to prove none of them belongs to the Edge browser tree).
function WebView2Snapshot {
    @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.SessionId -eq $sid } | ForEach-Object {
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($_.ParentProcessId)" -ErrorAction SilentlyContinue
        [pscustomobject]@{ Id = $_.ProcessId; Created = $_.CreationDate; Parent = if ($parent) { $parent.Name } else { '(terminé)' } }
    })
}
function Zoom-Checks($win, [string]$path) {
    # Checkbox column of the visible explorer rows, enlarged x3.
    $list = Find-Id $win 'ExplorerList'
    $r = $list.Current.BoundingRectangle
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [Shot]::Zoom($hwnd, [int]$r.X, [int]$r.Y, [int]([Math]::Min(420, $r.Width)), [int]$r.Height, 3, $path)
}
function Open-Explorer($win) {
    Go-Page $win 'Nav_Personal'
    $deadline = (Get-Date).AddSeconds(120)
    while ((Val $win 'FooterSelection') -match 'calcul' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Start-Sleep -Seconds 1
}

# ------------------------------------------------------------------ browsers open
Log "Ouverture de Chrome, Edge et Firefox…"
Start-Process 'C:\Program Files\Google\Chrome\Application\chrome.exe' 'about:blank' | Out-Null
Start-Process 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe' 'about:blank' | Out-Null
Start-Process 'C:\Program Files\Mozilla Firefox\firefox.exe' 'about:blank' | Out-Null
Start-Sleep -Seconds 8
Log ("Processus (session $sid) avant : " + (Counts))

# ------------------------------------------------------------------ launch (Release, no elevation)
$consent = $false
$win = Start-App (Resolve-Path $Exe).Path
for ($i = 0; $i -lt 8; $i++) { if (Get-Process consent -ErrorAction SilentlyContinue) { $consent = $true }; Start-Sleep -Milliseconds 250 }
$proc = Get-Process 'DataBridge' | Select-Object -First 1
Log "Exe : $($proc.Path)"
Log "Jeton : $([UiNative]::TokenInfo($proc.Id))  | UAC vu : $consent"
Log ("Titre : " + $win.Current.Name)
$logo = Find-Id $win 'HeaderLogo'
$lr = $logo.Current.BoundingRectangle
Log ("Logo en-tête : {0:N0} x {1:N0} px, rapport {2:N2} (contenu du PNG : 6,34)" -f $lr.Width, $lr.Height, ($lr.Width / $lr.Height))
Log ("Baseline : " + (Val $win 'HeaderBaseline') + " | " + (Val $win 'VersionText'))
Start-Sleep -Seconds 1
Save-Shot $win "$Shots\01-accueil.png" | Out-Null

# ------------------------------------------------------------------ destination
Set-Destination $win $Destination
Log ("Destination : " + (Val $win 'BackupFolderPreview') + " -> " + (Val $win 'DestinationCheck'))
Save-Shot $win "$Shots\02-destination.png" | Out-Null

# ------------------------------------------------------------------ personal folders + partial selection
Open-Explorer $win
foreach ($e in (Find-All $win 'RootLocal')) { Log ("Local : " + $e.Current.Name) }
foreach ($e in (Find-All $win 'RootCloud')) { Log ("En ligne : " + $e.Current.Name) }
Save-Shot $win "$Shots\03-dossiers-personnels.png" | Out-Null
Expand-Row $win 'Documents' | Out-Null
Expand-Row $win 'Virtual Machines' | Out-Null
Expand-Row $win 'WS2025' | Out-Null
$big = '564d34a0-f4da-8cca-5762-0feaf5e6af75.vmem'
Toggle-El (Get-RowCheck $win $big)
Start-Sleep -Seconds 2
Log ("Sélection partielle : $big=" + (Get-ToggleState (Get-RowCheck $win $big)) + " WS2025=" + (Get-ToggleState (Get-RowCheck $win 'WS2025')) + " Documents=" + (Get-ToggleState (Get-RowCheck $win 'Documents')))
Find-Row $win 'Documents' | Out-Null
Start-Sleep -Milliseconds 600
Save-Shot $win "$Shots\04-dossier-developpe-selection-partielle.png" | Out-Null
Zoom-Checks $win "$Shots\05-etat-partiel-checkbox-125pct-zoom.png"

# ------------------------------------------------------------------ browsers page
Go-Page $win 'Nav_Browsers'
Start-Sleep -Seconds 4   # running state refreshed every 3 s
$bv = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'BrowsersView')))
$bt = (Texts $bv) -replace '\s+', ' '
Log ("Navigateurs : " + $bt.Substring(0, [Math]::Min(400, $bt.Length)))
Save-Shot $win "$Shots\06-navigateurs.png" | Out-Null

# ------------------------------------------------------------------ summary
Go-Page $win 'Nav_Summary'
Start-Sleep -Seconds 2
foreach ($id in 'SummaryEstimated', 'SummaryPlannedFiles', 'SummarySelectedFiles', 'SummaryPlannedCount', 'SummaryCloudFiles', 'SummaryCloudFate', 'SummaryFree') { Log ("$id : " + (Val $win $id)) }
Save-Shot $win "$Shots\07-resume.png" | Out-Null

# ------------------------------------------------------------------ 100 % DPI pass (second monitor), then back
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[Shot]::Move($hwnd, $Monitor100X, $Monitor100Y)
Start-Sleep -Seconds 2
Save-Shot $win "$Shots\dpi100-resume.png" | Out-Null
Open-Explorer $win
Find-Row $win 'Documents' | Out-Null
Start-Sleep -Milliseconds 600
Save-Shot $win "$Shots\dpi100-selection-partielle.png" | Out-Null
Zoom-Checks $win "$Shots\dpi100-etat-partiel-checkbox-zoom.png"
Go-Page $win 'Nav_Home'
Start-Sleep -Milliseconds 800
Save-Shot $win "$Shots\dpi100-accueil.png" | Out-Null
[Shot]::Move($hwnd, 280, 160)
Start-Sleep -Seconds 2
Go-Page $win 'Nav_Summary'
Start-Sleep -Seconds 2

# ------------------------------------------------------------------ launch -> DataBridge dialog -> force close
Log ("Avant lancement : " + (Counts))
Invoke-El (Find-Id $win 'FooterStartCopyButton')
$dlg = Find-Window $win 'BrowserCloseDialog'
if (-not $dlg) { Log 'ERREUR : fenêtre de fermeture des navigateurs introuvable'; return }
$items = @($dlg.FindAll($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, 'DialogItem'))) | ForEach-Object { $_.Current.Name })
Log ("Dialogue : " + (Val $dlg 'DialogHeading') + " | navigateurs : " + ($items -join ', '))
Log ("Avertissement : " + ((Val $dlg 'DialogWarning') -replace "`r?`n", ' / '))
Log ("Question : " + (Val $dlg 'DialogQuestion') + " | boutons : " + (Val $dlg 'DialogSecondary') + " / " + (Val $dlg 'DialogPrimary'))
Start-Sleep -Milliseconds 400
Save-Shot $dlg "$Shots\08-modale-fermeture-navigateurs.png" | Out-Null
$wvBefore = WebView2Snapshot
Log ("msedgewebview2 avant : $($wvBefore.Count) processus, parents : " + (($wvBefore | Group-Object Parent | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ', '))
Invoke-El (Find-Id $dlg 'DialogPrimary')

$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500 } while (((CountP 'chrome') + (CountP 'msedge') + (CountP 'firefox')) -gt 0 -and (Get-Date) -lt $deadline)
Log ("Après confirmation : " + (Counts))
$alive = @($wvBefore | Where-Object { $p = Get-Process -Id $_.Id -ErrorAction SilentlyContinue; $p -and $p.ProcessName -eq 'msedgewebview2' })
Log ("msedgewebview2 : $($alive.Count) / $($wvBefore.Count) processus d'avant toujours actifs (mêmes PID)")

# ------------------------------------------------------------------ copy
$deadline = (Get-Date).AddSeconds(120)
do { Start-Sleep -Milliseconds 400; $pct = [int]('0' + ((Val $win 'CopyPercent' 1) -replace '[^\d]', '')) } while ($pct -lt 30 -and (Get-Date) -lt $deadline)
Log ("Copie : " + (Val $win 'CopyCategory') + " | " + (Val $win 'CopyFiles') + " | " + (Val $win 'CopyBytes') + " | " + (Val $win 'CopyPercent'))
Log ("Copie : " + (Val $win 'CopySelection') + " | erreurs " + (Val $win 'CopyErrors') + " | ignorés " + (Val $win 'CopyIgnored') + " | cloud " + (Val $win 'CopyCloud'))
Save-Shot $win "$Shots\09-copie-en-cours.png" | Out-Null

$deadline = (Get-Date).AddMinutes(25)
$title = $null
while (-not $title -and (Get-Date) -lt $deadline) {
    try { $t = Find-Id $win 'ResultTitle' 1; if ($t.Current.Name) { $title = $t.Current.Name } } catch { }
    Start-Sleep -Seconds 1
}
Start-Sleep -Seconds 1
Log "Résultat : $title"
Log ("Chiffres clés : fichiers " + (Val $win 'ResultFiles') + " | volume " + (Val $win 'ResultBytes') + " | erreurs " + (Val $win 'ResultErrors') + " | durée " + (Val $win 'ResultDuration'))
Log ("Traitement : " + (Val $win 'ResultProcessed'))
Log ("Contrôle : " + ((Val $win 'ResultControl') -replace "`r?`n", ' / '))
$lines = @((Find-Id $win 'ResultLines').FindAll($TS::Descendants, $All) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 1 })
Log ("Données : " + ($lines -join ' | '))
$wifi = @((Find-Id $win 'ResultWifi').FindAll($TS::Descendants, $All) | Where-Object { $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' } | ForEach-Object { $_.Current.Name } | Where-Object { $_.Length -gt 1 })
Log ("Wi-Fi : " + ($wifi -join ' | '))
Save-Shot $win "$Shots\10-resultat-final.png" | Out-Null
Log ("Processus navigateurs restants (session) : " + (Counts))
$p2 = Get-Process 'DataBridge' | Select-Object -First 1
Log "Jeton en fin de test : $([UiNative]::TokenInfo($p2.Id)) | UAC vu pendant le test : $([bool](Get-Process consent -ErrorAction SilentlyContinue))"
