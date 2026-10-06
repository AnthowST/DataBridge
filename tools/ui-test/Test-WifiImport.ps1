# DataBridge 2.0 — Wi-Fi restore check (Windows PowerShell 5.1, NOT elevated).
# Imports a throw-away open-network profile "DataBridge-Test-<random>" with WifiRestoreService (same code as the
# restore), checks that Windows lists it, then deletes it. The real Wi-Fi profiles of the PC are never modified.
param([string]$CoreDll = "$PSScriptRoot\..\..\src\DataBridge.Core\bin\Debug\net10.0-windows\DataBridge.Core.dll")
$ErrorActionPreference = 'Stop'
$name = 'DataBridge-Test-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$dir = Join-Path $env:TEMP $name
New-Item -ItemType Directory -Force $dir | Out-Null
$xml = Join-Path $dir "Wi-Fi-$name.xml"
$hex = -join ([Text.Encoding]::UTF8.GetBytes($name) | ForEach-Object { $_.ToString('X2') })
@"
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
  <name>$name</name>
  <SSIDConfig><SSID><hex>$hex</hex><name>$name</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>manual</connectionMode>
  <MSM><security><authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption></security></MSM>
</WLANProfile>
"@ | Set-Content -Encoding UTF8 $xml

# .NET 10 assembly: run the check in a small dotnet process (PowerShell 5.1 hosts .NET Framework only).
$proj = Join-Path $dir 'check'
New-Item -ItemType Directory -Force $proj | Out-Null
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><Reference Include="DataBridge.Core"><HintPath>$CoreDll</HintPath></Reference></ItemGroup>
</Project>
"@ | Set-Content -Encoding UTF8 (Join-Path $proj 'check.csproj')
@"
using System.Reflection;
using DataBridge.Core.Restore;
var name = args[0]; var xml = args[1];
var before = WifiRestoreService.Inspect();
Console.WriteLine(`$"WLAN disponible : {before.Available} | profils avant : {before.ExistingProfiles.Count} | test présent avant : {before.ExistingProfiles.Contains(name)}");
var profile = new BackupWifiProfile(name, "Network\\WiFi\\x.xml", xml, "Ouvert", false, null);
var r = WifiRestoreService.Import(profile, WifiExistingPolicy.Keep);
Console.WriteLine(`$"Import : {r.Outcome} — {r.Message}");
var after = WifiRestoreService.Inspect();
Console.WriteLine(`$"Profil visible après import : {after.ExistingProfiles.Contains(name)} | profils après : {after.ExistingProfiles.Count}");
var again = WifiRestoreService.Import(profile, WifiExistingPolicy.Keep);
Console.WriteLine(`$"Second import (déjà présent, conserver) : {again.Outcome} — {again.Message}");
var delete = typeof(WifiRestoreService).GetMethod("Delete", BindingFlags.NonPublic | BindingFlags.Static)!;
Console.WriteLine(`$"Nettoyage : supprimé = {delete.Invoke(null, [name])}");
var final = WifiRestoreService.Inspect();
Console.WriteLine(`$"Profil encore présent : {final.ExistingProfiles.Contains(name)} | profils à la fin : {final.ExistingProfiles.Count} (avant : {before.ExistingProfiles.Count})");
"@ | Set-Content -Encoding UTF8 (Join-Path $proj 'Program.cs')
Copy-Item $CoreDll $proj
dotnet run --project $proj -v q -- $name $xml
Remove-Item -Recurse -Force $dir
