# DataBridge 1.3.2 — layout stability when checking / unchecking (Windows PowerShell 5.1, NOT elevated).
# For every toggle of a check box (Signatures Outlook, Navigateurs, Réseaux Wi-Fi, Fond d'écran) the script compares,
# before and after: (1) the position of every UI Automation element of the page, (2) the pixels of the page.
# Only the check boxes themselves and the size / selection counters of the browser cards may change.
# The check box gets the keyboard focus first (exactly like a mouse click), then is toggled.
param(
    [string]$Exe = "$PSScriptRoot\..\..\publish\DataBridge.exe",
    [string]$Out = "$PSScriptRoot\..\..\docs\screenshots\databridge-1.3.2",
    [string]$Tag = 'apres',
    [switch]$Monitor100,
    [int]$Monitor100X = -1880, [int]$Monitor100Y = 20, [int]$Monitor100W = 1700, [int]$Monitor100H = 990
)
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing @'
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Lay {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
  public static bool PerMonitor() { bool ok = SetProcessDpiAwarenessContext(new IntPtr(-4)); SetThreadDpiAwarenessContext(new IntPtr(-4)); return ok; }
  public static int[] Frame(IntPtr h) { RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return new[] { r.L, r.T, r.R, r.B }; }
  public static uint Dpi(IntPtr h) { return GetDpiForWindow(h); }
  // Large restored window on the 100 % monitor (a maximised window there was sent back to the primary monitor).
  public static void Place(IntPtr h, int x, int y, int w, int hh) { ShowWindow(h, 9); SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004); System.Threading.Thread.Sleep(800); SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0004); }
  public static void Maximize(IntPtr h) { ShowWindow(h, 3); }

  // Pixels that differ inside 'region' but outside every 'allowed' rectangle (all in image coordinates, x,y,w,h groups).
  public static string Diff(string a, string b, int[] region, int[] allowed, string overlay) {
    int[] pa, pb; int w, h;
    using (var A = new Bitmap(a)) using (var B = new Bitmap(b)) {
      if (A.Width != B.Width || A.Height != B.Height) return "taille-differente";
      w = A.Width; h = A.Height; pa = Pixels(A); pb = Pixels(B);
    }
    var reg = Rectangle.Intersect(new Rectangle(region[0], region[1], region[2], region[3]), new Rectangle(0, 0, w, h));
    var ok = new List<Rectangle>();
    for (int i = 0; i + 3 < allowed.Length; i += 4) ok.Add(new Rectangle(allowed[i], allowed[i + 1], allowed[i + 2], allowed[i + 3]));
    int inside = 0, outside = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
    var o = new int[pb.Length];
    for (int k = 0; k < pb.Length; k++) { Color q = Color.FromArgb(pb[k]); o[k] = Color.FromArgb(255, (q.R + 510) / 3, (q.G + 510) / 3, (q.B + 510) / 3).ToArgb(); }
    for (int y = reg.Top; y < reg.Bottom; y++) for (int x = reg.Left; x < reg.Right; x++) {
      int k = y * w + x;
      if (pa[k] == pb[k]) continue;
      bool allowedPx = false; foreach (var r in ok) if (r.Contains(x, y)) { allowedPx = true; break; }
      if (allowedPx) { inside++; o[k] = Color.FromArgb(255, 0, 150, 255).ToArgb(); }
      else { outside++; o[k] = Color.FromArgb(255, 230, 0, 0).ToArgb(); if (x < x0) x0 = x; if (y < y0) y0 = y; if (x > x1) x1 = x; if (y > y1) y1 = y; }
    }
    if (overlay != null) using (var ob = new Bitmap(w, h, PixelFormat.Format32bppArgb)) {
      var bd = ob.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
      Marshal.Copy(o, 0, bd.Scan0, o.Length); ob.UnlockBits(bd); ob.Save(overlay, ImageFormat.Png);
    }
    return "pixels case/compteur=" + inside + " | pixels ailleurs=" + outside + (outside > 0 ? " zone=" + x0 + "," + y0 + "-" + x1 + "," + y1 : "");
  }
  static int[] Pixels(Bitmap bmp) {
    var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var px = new int[bmp.Width * bmp.Height]; Marshal.Copy(bd.Scan0, px, 0, px.Length); bmp.UnlockBits(bd); return px;
  }

  // Before / after / differences of one section, stacked, for the report.
  public static void Sheet(string a, string b, string d, int[] crop, string[] labels, string path) {
    using (var A = new Bitmap(a)) using (var B = new Bitmap(b)) using (var D = new Bitmap(d)) {
      var c = Rectangle.Intersect(new Rectangle(crop[0], crop[1], crop[2], crop[3]), new Rectangle(0, 0, A.Width, A.Height));
      // Trim the empty background below the content (whole-page sections).
      int bg = A.GetPixel(c.Left + c.Width / 2, c.Bottom - 30).ToArgb(), last = c.Top;
      for (int y = c.Bottom - 30; y >= c.Top && last == c.Top; y--)
        for (int x = c.Left + 20; x < c.Right - 20; x += 2)
          if (A.GetPixel(x, y).ToArgb() != bg || B.GetPixel(x, y).ToArgb() != bg) { last = y; break; }
      c.Height = Math.Min(c.Height, last - c.Top + 24);
      int lh = 30, gap = 14;
      using (var s = new Bitmap(c.Width, 3 * (c.Height + lh) + 2 * gap))
      using (var g = Graphics.FromImage(s))
      using (var f = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel)) {
        g.Clear(Color.White);
        var imgs = new[] { A, B, D };
        for (int i = 0; i < 3; i++) {
          int y = i * (c.Height + lh + gap);
          g.FillRectangle(new SolidBrush(Color.FromArgb(0, 33, 77)), 0, y, c.Width, lh);
          g.DrawString(labels[i], f, Brushes.White, 8, y + 7);
          g.DrawImage(imgs[i], new Rectangle(0, y + lh, c.Width, c.Height), c, GraphicsUnit.Pixel);
        }
        s.Save(path, ImageFormat.Png);
      }
    }
  }
}
'@
$perMonitor = [Lay]::PerMonitor()
Import-Module "$PSScriptRoot\UiTest.psm1" -Force -WarningAction SilentlyContinue
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$All = [System.Windows.Automation.Condition]::TrueCondition
$CheckCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)

$win = Start-App $Exe
# 2.0: DataBridge opens on its home screen; this scenario checks the backup wizard.
try { Invoke-El (Find-Id $win 'ModeBackupButton' 5); Start-Sleep -Seconds 2 } catch { }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
if ($Monitor100) { [Lay]::Place($hwnd, $Monitor100X, $Monitor100Y, $Monitor100W, $Monitor100H) } else { [Lay]::Maximize($hwnd) }
Start-Sleep -Seconds 2
$dpi = [Lay]::Dpi($hwnd); $s = $dpi / 96.0
$dir = Join-Path $Out ("$Tag-{0}pct" -f [int]($s * 100))
New-Item -ItemType Directory -Force $dir | Out-Null
$report = New-Object System.Collections.Generic.List[string]
function Log($m) { $report.Add($m); Write-Host $m }
Log ("{0} | exe {1} | version {2} | DPI {3} ({4} %) | per-monitor={5} | jeton {6}" -f $Tag, $Exe, (Find-Id $win 'VersionText').Current.Name, $dpi, [int]($s * 100), $perMonitor, [UiNative]::TokenInfo((Get-Process DataBridge).Id))
$f = [Lay]::Frame($hwnd); $wr = $win.Current.BoundingRectangle
Log ("Fenêtre DWM {0},{1}-{2},{3} | UIA {4},{5}" -f $f[0], $f[1], $f[2], $f[3], $wr.X, $wr.Y)

function Page-Host {
    # The page's own ScrollViewer (right of the 240 DIP sidebar).
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'ScrollViewer')
    $deadline = (Get-Date).AddSeconds(10)
    do {
        $frame = [Lay]::Frame($hwnd)
        foreach ($e in $win.FindAll($TS::Descendants, $cond)) { if ($e.Current.BoundingRectangle.X -gt $frame[0] + 200 * $s) { return $e } }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw 'Zone de page introuvable'
}
function Snapshot {
    $list = @(); $i = 0
    foreach ($e in (Page-Host).FindAll($TS::Descendants, $All)) {
        $r = $e.Current.BoundingRectangle
        if ($r.IsEmpty) { continue }
        # Counters (text expected to change; an empty TextBlock is not exposed to UI Automation): checked by pixels only.
        if ($e.Current.AutomationId -like 'Browser*Text' -or (Is-Counter $e.Current.Name)) { $script:counters.Add($r); continue }
        $list += [pscustomobject]@{ Key = "$i|$($e.Current.ControlType.ProgrammaticName)|$($e.Current.AutomationId)"; Name = $e.Current.Name; X = $r.X; Y = $r.Y; W = $r.Width; H = $r.Height }
        $i++
    }
    return $list
}
function Is-Counter([string]$n) { return $n -match 'profil\(s\) sélectionné\(s\)|^≈|^Calcul|\d+(\.\d+)? (octets|Ko|Mo|Go) · ' }
function Compare-Snap($a, $b) {
    $moved = @()
    if ($a.Count -ne $b.Count) { return @("structure différente : $($a.Count) éléments avant, $($b.Count) après") }
    for ($k = 0; $k -lt $a.Count; $k++) {
        $p = $a[$k]; $q = $b[$k]
        if ($p.Key -ne $q.Key) { return @("structure différente à l'élément $k") }
        if ((Is-Counter $p.Name) -or (Is-Counter $q.Name)) { continue }
        $dx = $q.X - $p.X; $dy = $q.Y - $p.Y; $dw = $q.W - $p.W; $dh = $q.H - $p.H
        if ([math]::Abs($dx) -gt 0.01 -or [math]::Abs($dy) -gt 0.01 -or [math]::Abs($dw) -gt 0.01 -or [math]::Abs($dh) -gt 0.01) {
            $moved += ("{0} « {1} » dx={2} dy={3} dw={4} dh={5}" -f $p.Key.Split('|')[1].Replace('ControlType.', ''), $p.Name, $dx, $dy, $dw, $dh)
        }
    }
    return $moved
}
function Img-Rect($r, [double]$pad = 0) {
    $fr = [Lay]::Frame($hwnd)
    return @([int][math]::Floor($r.X - $fr[0] - $pad), [int][math]::Floor($r.Y - $fr[1] - $pad), [int][math]::Ceiling($r.Width + 2 * $pad), [int][math]::Ceiling($r.Height + 2 * $pad))
}
function Allowed($snapA, $snapB) {
    # Every check box square (with room for its focus ring) + the counters of the browser cards (before and after).
    $rects = @()
    foreach ($e in (Page-Host).FindAll($TS::Descendants, $CheckCond)) {
        $r = $e.Current.BoundingRectangle
        $cy = $r.Y + $r.Height / 2
        $rects += Img-Rect ([System.Windows.Rect]::new($r.X - 2 * $s, $cy - 15 * $s, 26 * $s, 30 * $s)) 1
    }
    foreach ($t in $script:counters) { $rects += Img-Rect $t 2 }
    return [int[]]$rects
}
$script:n = 0
$script:counters = New-Object System.Collections.Generic.List[System.Windows.Rect]
function Shot([string]$name) { $script:n++; $p = Join-Path $dir ('{0:00}-{1}.png' -f $script:n, $name); Save-Shot $win $p | Out-Null; return $p }
function Wait-Estimate { $d = (Get-Date).AddSeconds(8); do { Start-Sleep -Milliseconds 400 } while ((Texts) -match 'Calcul…' -and (Get-Date) -lt $d) }
function Texts { ((Page-Host).FindAll($TS::Descendants, $All) | ForEach-Object { $_.Current.Name }) -join '|' }

$fails = 0
# One toggle: the box gets the focus (as with a mouse click) and is toggled; positions + pixels compared.
function Step($cb, [string]$caseName, [string]$beforePath, $sectionEl, [string]$labelBefore, [string]$labelAfter) {
    $script:counters.Clear()
    $snapA = Snapshot
    $cb.SetFocus(); Start-Sleep -Milliseconds 200
    Toggle-El $cb
    Wait-Estimate; Start-Sleep -Milliseconds 600
    $after = Shot $caseName
    $snapB = Snapshot
    $moved = Compare-Snap $snapA $snapB
    $hostRect = Img-Rect (Page-Host).Current.BoundingRectangle
    $diffPath = $after -replace '\.png$', '-diff.png'
    if ($beforePath -is [array] -or $after -is [array]) { Write-Host "DEBUG avant=[$beforePath] apres=[$after]" }
    $px = [Lay]::Diff($beforePath, $after, $hostRect, (Allowed $snapA $snapB), $diffPath)
    $state = Get-ToggleState $cb
    $ok = ($moved.Count -eq 0) -and ($px -match 'pixels ailleurs=0')
    if (-not $ok) { $script:fails++ }
    Log ("[{0}] {1} -> {2} | éléments déplacés : {3} | {4}" -f ($(if ($ok) { 'OK ' } else { 'ÉCHEC' })), $caseName, $state, $moved.Count, $px)
    foreach ($m in ($moved | Select-Object -First 8)) { Log "        $m" }
    if ($sectionEl) {
        $sr = if ($sectionEl -is [System.Windows.Rect]) { $sectionEl } else { $sectionEl.Current.BoundingRectangle }
        $crop = Img-Rect $sr (10 * $s)
        [Lay]::Sheet($beforePath, $after, $diffPath, $crop, @("AVANT : $labelBefore", "APRÈS : $labelAfter", 'DIFFÉRENCES : bleu = case / compteur autorisés, rouge = déplacement interdit'), ($after -replace '\.png$', '-comparaison.png'))
    }
    Remove-Item $diffPath
    return $after
}
function Fresh([string]$navId) {
    # Re-open the page: no focused element, scroll at the top (identical starting point for every case).
    Go-Page $win 'Nav_Home'; Go-Page $win $navId; Start-Sleep -Milliseconds 900
}
function Set-State($cb, [string]$want) { if ((Get-ToggleState $cb) -ne $want) { Toggle-El $cb; Start-Sleep -Milliseconds 300 } }

# ------------------------------------------------------------------ Navigateurs
# The cards are Borders (not exposed to UI Automation): a card = from its title to the next card's title.
$Titles = [ordered]@{ Chrome = 'Google Chrome'; Edge = 'Microsoft Edge'; Firefox = 'Mozilla Firefox' }
function Card([string]$b) {
    $tops = @{}
    foreach ($k in $Titles.Keys) {
        $t = $win.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Titles[$k])))
        if ($t) { $tops[$k] = $t.Current.BoundingRectangle.Y }
    }
    $top = $tops[$b] - 26 * $s
    $next = @($tops.Values | Where-Object { $_ -gt $tops[$b] } | Sort-Object)
    $hostR = (Page-Host).Current.BoundingRectangle
    $bottom = if ($next.Count -gt 0) { $next[0] - 32 * $s } else { $hostR.Bottom }
    $boxes = @((Page-Host).FindAll($TS::Descendants, $CheckCond) | Where-Object { $r = $_.Current.BoundingRectangle; $r.Y -gt $top -and $r.Y -lt $bottom } | Sort-Object { $_.Current.BoundingRectangle.Y })
    if ($next.Count -eq 0 -and $boxes.Count -gt 0) { $bottom = [math]::Min($hostR.Bottom, $boxes[-1].Current.BoundingRectangle.Bottom + 50 * $s) }
    $rect = [System.Windows.Rect]::new($hostR.X + 20 * $s, $top, $hostR.Width - 40 * $s, $bottom - $top)
    return [pscustomobject]@{ Boxes = $boxes; Rect = $rect }
}
Go-Page $win 'Nav_Browsers'
foreach ($b in 'Chrome', 'Edge') {
    $c = Card $b
    if ($c.Boxes.Count -eq 0) { Log "$b : aucun profil (non détecté) — cas ignoré"; continue }
    foreach ($x in $c.Boxes) { Set-State $x 'Off' }
    Wait-Estimate; Fresh 'Nav_Browsers'
    $c = Card $b
    $before = Shot "$($b.ToLower())-decoche"
    $after = Step $c.Boxes[0] "$($b.ToLower())-coche" $before $c.Rect "$b décoché" "$b coché"
    Step $c.Boxes[0] "$($b.ToLower())-decoche-de-nouveau" $after $c.Rect "$b coché" "$b décoché" | Out-Null
}
$c = Card 'Firefox'
$fx = $c.Boxes
if ($fx.Count -ge 2) {
    $n1 = $fx[0].Current.Name; $n2 = $fx[1].Current.Name
    foreach ($x in $fx) { Set-State $x 'Off' }
    Wait-Estimate; Fresh 'Nav_Browsers'
    $c = Card 'Firefox'; $fx = $c.Boxes; $card = $c.Rect
    $snapA = Snapshot
    $pA = Shot 'firefox-A-aucun'
    $pB = Step $fx[0] 'firefox-B-premier-seul' $pA $card "[ ] $n1  [ ] $n2" "[x] $n1  [ ] $n2"
    $pD = Step $fx[1] 'firefox-D-les-deux' $pB $card "[x] $n1  [ ] $n2" "[x] $n1  [x] $n2"
    $pB2 = Step $fx[1] 'firefox-B-deuxieme-decoche' $pD $card "[x] $n1  [x] $n2" "[x] $n1  [ ] $n2"
    $pA2 = Step $fx[0] 'firefox-A-aucun' $pB2 $card "[x] $n1  [ ] $n2" "[ ] $n1  [ ] $n2"
    $pC = Step $fx[1] 'firefox-C-deuxieme-seul' $pA2 $card "[ ] $n1  [ ] $n2" "[ ] $n1  [x] $n2"
    $moved = Compare-Snap $snapA (Snapshot)
    Log ("Firefox état C comparé à l'état A : éléments déplacés : {0}" -f $moved.Count)
    # Vertical alignment of each profile row: centre of the square vs centre of the profile name.
    foreach ($x in $fx) {
        $r = $x.Current.BoundingRectangle
        $t = $x.FindAll($TS::Descendants, $All) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Text } | Select-Object -First 1
        $tr = $t.Current.BoundingRectangle
        Log ("Ligne « {0} » : hauteur {1:0.##} px, centre case {2:0.##}, centre texte « {3} » {4:0.##} (écart {5:0.##} px)" -f $x.Current.Name, $r.Height, ($r.Y + $r.Height / 2), $t.Current.Name, ($tr.Y + $tr.Height / 2), (($tr.Y + $tr.Height / 2) - ($r.Y + $r.Height / 2)))
    }
} else { Log "Firefox : moins de deux profils — cas A/B/C/D ignoré" }

# ------------------------------------------------------------------ Signatures Outlook
Fresh 'Nav_Outlook'
$cb = Find-Id $win 'IncludeSignatures'
if ($cb.Current.IsEnabled) {
    $sectionEl = (Page-Host)
    $p0 = Shot "signatures-$((Get-ToggleState $cb).ToLower())"
    $p1 = Step $cb 'signatures-bascule-1' $p0 $sectionEl 'état initial' 'après clic'
    Step $cb 'signatures-bascule-2' $p1 $sectionEl 'après clic' 'après second clic' | Out-Null
} else { Log 'Signatures : aucune signature (case désactivée) — cas ignoré' }

# ------------------------------------------------------------------ Réseaux Wi-Fi
Fresh 'Nav_Wifi'
$d = (Get-Date).AddSeconds(20); do { Start-Sleep -Milliseconds 500; $w = @(Find-All $win 'WifiCheck') } while ($w.Count -eq 0 -and (Get-Date) -lt $d)
if ($w.Count -gt 0) {
    $list = (Page-Host)
    $p0 = Shot 'wifi-initial'
    $p1 = Step $w[0] 'wifi-premier-bascule' $p0 $list 'état initial' "« $($w[0].Current.Name) » basculé"
    $p2 = Step $w[0] 'wifi-premier-retour' $p1 $list "« $($w[0].Current.Name) » basculé" 'retour à l''état initial'
    if ($w.Count -gt 1) {
        $p3 = Step $w[1] 'wifi-second-bascule' $p2 $list 'état initial' "« $($w[1].Current.Name) » basculé"
        Step $w[1] 'wifi-second-retour' $p3 $list "« $($w[1].Current.Name) » basculé" 'retour à l''état initial' | Out-Null
    }
} else { Log 'Wi-Fi : aucun profil — cas ignoré' }

# ------------------------------------------------------------------ Fond d'écran
Fresh 'Nav_Wallpaper'
$cb = Find-Id $win 'IncludeWallpaper'
if ($cb.Current.IsEnabled) {
    Start-Sleep -Seconds 1 # thumbnail
    $p0 = Shot 'fond-ecran-initial'
    $p1 = Step $cb 'fond-ecran-bascule-1' $p0 (Page-Host) 'état initial' 'après clic'
    Step $cb 'fond-ecran-bascule-2' $p1 (Page-Host) 'après clic' 'après second clic' | Out-Null
} else { Log 'Fond d''écran : non détecté — cas ignoré' }

$f2 = [Lay]::Frame($hwnd)
if ([Lay]::Dpi($hwnd) -ne $dpi -or $f2[0] -ne $f[0] -or $f2[1] -ne $f[1]) { $fails++; Log ("ATTENTION : la fenêtre a changé d'écran ou de DPI pendant le test ({0},{1} DPI {2})" -f $f2[0], $f2[1], [Lay]::Dpi($hwnd)) }
else { Log ("Fenêtre restée au même endroit pendant tout le test : {0},{1}-{2},{3}, DPI {4}" -f $f2[0], $f2[1], $f2[2], $f2[3], $dpi) }
Log ("RÉSULTAT {0} : {1} bascule(s) avec déplacement" -f $Tag, $fails)
$report | Set-Content -Encoding UTF8 (Join-Path $dir 'rapport.txt')
(Get-Process DataBridge -ErrorAction SilentlyContinue) | ForEach-Object { $_.CloseMainWindow() | Out-Null }
