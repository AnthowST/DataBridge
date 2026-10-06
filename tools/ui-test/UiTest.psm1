# UI test helpers for DataBridge: drives the real WPF window through Windows UI Automation,
# takes window screenshots (PrintWindow) and inspects the process token (elevation / integrity).
# Windows PowerShell 5.1:  Import-Module .\tools\ui-test\UiTest.psm1 -Force

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

if (-not ('UiNative' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class UiNative
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, string l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    public static void SetText(IntPtr h, string text) { SendMessage(h, 0x000C, IntPtr.Zero, text); }
    public static void ClickOk(IntPtr dlg, IntPtr button) { PostMessage(dlg, 0x0111, new IntPtr(1), button); }
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public static void Capture(IntPtr hwnd, string path)
    {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels on every monitor
        RECT wr; GetWindowRect(hwnd, out wr);
        RECT fr; if (DwmGetWindowAttribute(hwnd, 9, out fr, Marshal.SizeOf(typeof(RECT))) != 0) fr = wr;
        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
                g.ReleaseHdc(hdc);
            }
            var crop = Rectangle.Intersect(new Rectangle(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - fr.Left, fr.Bottom - fr.Top), new Rectangle(0, 0, w, h));
            using (var outBmp = bmp.Clone(crop, PixelFormat.Format32bppArgb)) outBmp.Save(path, ImageFormat.Png);
        }
    }

    // Returns "elevated=<0|1>;integrity=<RID hex>"
    public static string TokenInfo(int pid)
    {
        IntPtr p = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (p == IntPtr.Zero) return "open-process-failed";
        try
        {
            IntPtr t; if (!OpenProcessToken(p, 0x8, out t)) return "open-token-failed";
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(256); int ret;
                GetTokenInformation(t, 20, buf, 4, out ret); int elevated = Marshal.ReadInt32(buf);
                GetTokenInformation(t, 25, buf, 256, out ret); // TokenIntegrityLevel
                IntPtr sid = Marshal.ReadIntPtr(buf);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                Marshal.FreeHGlobal(buf);
                return "elevated=" + elevated + ";integrity=0x" + rid.ToString("X4");
            }
            finally { CloseHandle(t); }
        }
        finally { CloseHandle(p); }
    }
}
'@
}

$script:AE = [System.Windows.Automation.AutomationElement]
$script:TS = [System.Windows.Automation.TreeScope]

function Get-AppWindow([int]$TimeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $p = Get-Process 'DataBridge' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($p) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $p.Id)
            $w = $AE::RootElement.FindFirst($TS::Children, $cond)
            if ($w) { return $w }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "Fenetre DataBridge introuvable"
}

function Find-Id($root, [string]$id, [int]$TimeoutSec = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $el = $root.FindFirst($TS::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Élément '$id' introuvable"
}

function Find-Name($root, [string]$name, [int]$TimeoutSec = 10) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $el = $root.FindFirst($TS::Descendants, $cond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "Élément nommé '$name' introuvable"
}

function Find-All($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
    return $root.FindAll($TS::Descendants, $cond)
}

function Invoke-El($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); return }
    if ($el.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); return }
    throw "Aucun pattern d'action sur $($el.Current.Name)"
}

function Toggle-El($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); $p.Toggle()
}

function Get-ToggleState($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern); return $p.Current.ToggleState.ToString()
}

function Set-Text($el, [string]$text) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern); $p.SetValue($text)
}

function Get-Text($el) {
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { return $p.Current.Value }
    return $el.Current.Name
}

function Go-Page($win, [string]$navId) {
    $nav = Find-Id $win $navId
    Invoke-El $nav
    Start-Sleep -Milliseconds 600
}

function Save-Shot($win, [string]$path) {
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    [UiNative]::Capture($hwnd, $path)
    return $path
}

# Row of the explorer by display name (the ListBoxItem), scrolled into view.
function Find-Row($win, [string]$name, [int]$TimeoutSec = 15) {
    $list = Find-Id $win 'ExplorerList'
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)))
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    do {
        $item = $list.FindFirst($TS::Children, $cond)
        if (-not $item) {
            $icp = $null
            if ($list.TryGetCurrentPattern([System.Windows.Automation.ItemContainerPattern]::Pattern, [ref]$icp)) {
                $item = $icp.FindItemByProperty($null, $AE::NameProperty, $name)
                if ($item) {
                    $vp = $null
                    if ($item.TryGetCurrentPattern([System.Windows.Automation.VirtualizedItemPattern]::Pattern, [ref]$vp)) { $vp.Realize() }
                }
            }
        }
        if ($item) {
            $sp = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$sp)) { $sp.ScrollIntoView() }
            return $item
        }
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw "Ligne '$name' introuvable"
}

function Expand-Row($win, [string]$name) {
    $row = Find-Row $win $name
    $exp = Find-Id $row 'RowExpander' 3
    if ((Get-ToggleState $exp) -ne 'On') { Toggle-El $exp }
    Start-Sleep -Milliseconds 700
    return $row
}

function Get-RowCheck($win, [string]$name) {
    $row = Find-Row $win $name
    return Find-Id $row 'RowCheck' 3
}

# Native Windows file/folder dialog owned by the app: types a path in the name box and validates.
function Complete-FileDialog([string]$text, [int]$TimeoutSec = 15) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $dlg = $null
    $classCond = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, '#32770')
    while (-not $dlg -and (Get-Date) -lt $deadline) {
        $win = Get-AppWindow
        $dlg = $win.FindFirst($TS::Children, $classCond)
        if (-not $dlg) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $dlg) { throw "Boîte de dialogue introuvable" }
    $edit = $null
    while (-not $edit -and (Get-Date) -lt $deadline) {
        $edit = $dlg.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1152')))
        if (-not $edit) {
            # Open-file dialogs use a ComboBoxEx (1148) whose inner Edit holds the file name.
            $edit = $dlg.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1148')),
                (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Edit')))))
        }
        if (-not $edit) { Start-Sleep -Milliseconds 300 }
    }
    if (-not $edit) { throw "Champ nom de fichier introuvable" }
    Start-Sleep -Milliseconds 1200 # let the dialog finish initializing
    $ok = $dlg.FindFirst($TS::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, '1')))
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        [UiNative]::SetText([IntPtr]$edit.Current.NativeWindowHandle, $text)
        Start-Sleep -Milliseconds 400
        [UiNative]::ClickOk([IntPtr]$dlg.Current.NativeWindowHandle, [IntPtr]$ok.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 1200
        if (-not (Get-AppWindow).FindFirst($TS::Children, $classCond)) { break }
        # A folder picker may navigate into the typed folder instead of returning it: validate the current folder.
        if ($attempt -ge 0) { $text = '' }
    }
    # A file dialog that navigated into a folder (path typed = folder) stays open: validate again.
    $still = (Get-AppWindow).FindFirst($TS::Children, $classCond)
    return [bool]$still
}


function Start-App([string]$Exe = 'C:\Users\Anthow\Software\profilecopy\src\DataBridge.App\bin\Debug\net10.0-windows\DataBridge.exe') {
    Get-Process 'DataBridge' -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(4000) | Out-Null }
    Get-Process 'DataBridge' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process $Exe | Out-Null
    return Get-AppWindow 60
}

function Set-Destination($win, [string]$path) {
    Go-Page $win 'Nav_Destination'
    $btn = Find-Id $win 'ChooseFolderButton'
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Complete-FileDialog $path | Out-Null
    Start-Sleep -Seconds 2
}

Export-ModuleMember -Function *
