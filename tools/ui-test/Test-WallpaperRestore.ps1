# DataBridge 2.0 — wallpaper restore check (Windows PowerShell 5.1, NOT elevated).
# Restores a generated test image with WallpaperRestoreService (same code as the restore): copy to TranscodedWallpaper,
# stable copy, then SystemParametersInfo. Checks that Windows now points to it, then puts the original wallpaper back.
param([string]$CoreDll = "$PSScriptRoot\..\..\src\DataBridge.Core\bin\Debug\net10.0-windows\DataBridge.Core.dll")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = Join-Path $env:TEMP ('DataBridge-WallpaperTest-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Force $dir | Out-Null

# Test image without extension, like the TranscodedWallpaper cache of a backup.
$bmp = New-Object System.Drawing.Bitmap 960, 540
$g = [System.Drawing.Graphics]::FromImage($bmp)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Rectangle 0, 0, 960, 540),
    [System.Drawing.Color]::FromArgb(0, 33, 77), [System.Drawing.Color]::FromArgb(0, 211, 197), 0.0)
$g.FillRectangle($brush, 0, 0, 960, 540)
$g.DrawString('DataBridge — test de restauration du fond d''écran', (New-Object System.Drawing.Font 'Segoe UI', 28), [System.Drawing.Brushes]::White, 40, 230)
$g.Dispose()
$source = Join-Path $dir 'TranscodedWallpaper'
$bmp.Save($source, [System.Drawing.Imaging.ImageFormat]::Jpeg)
$bmp.Dispose()

$proj = Join-Path $dir 'check'
New-Item -ItemType Directory -Force $proj | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Reference Include="DataBridge.Core"><HintPath>$CoreDll</HintPath></Reference></ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 (Join-Path $proj 'check.csproj')
@"
using DataBridge.Core.Restore;
var source = args[0]; var dir = args[1];
var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
var original = WallpaperRestoreService.CurrentWallpaper();
Console.WriteLine(`$"Fond d'écran actuel : {original}");
var item = new WallpaperRestoreItem(source, @"Windows\Wallpaper\TranscodedWallpaper", transcoded, Path.Combine(dir, "store"), Apply: true);
var r = WallpaperRestoreService.Restore(item, Path.Combine(dir, "safety"));
Console.WriteLine(`$"Restauration : copié={r.Copied} appliqué={r.Applied} — {r.Message}");
Console.WriteLine(`$"Windows pointe vers la copie restaurée : {string.Equals(WallpaperRestoreService.CurrentWallpaper(), r.StoredPath, StringComparison.OrdinalIgnoreCase)} ({r.StoredPath})");
Console.WriteLine(`$"Copie de sécurité de l'ancien cache : {File.Exists(Path.Combine(dir, "safety", "TranscodedWallpaper"))}");
Thread.Sleep(3000);
var back = original != null && File.Exists(original) && WallpaperRestoreService.Apply(original);
Console.WriteLine(`$"Fond d'écran d'origine remis : {back} — actuel : {WallpaperRestoreService.CurrentWallpaper()}");
"@ | Set-Content -Encoding UTF8 (Join-Path $proj 'Program.cs')
Copy-Item $CoreDll $proj
dotnet run --project $proj -v q -- $source $dir
Remove-Item -Recurse -Force $dir
