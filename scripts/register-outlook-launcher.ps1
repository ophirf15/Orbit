#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and/or registers the thin Orbit Outlook launcher (orbit://push-outlook only).

.PARAMETER PayloadDir
  Optional folder that already contains Orbit.OutlookLauncher.dll (installer publish\outlook-launcher).
  When set, skips building from source.

.NOTES
  Close Outlook first when replacing the DLL. Prefer Settings → Install / Update in Orbit App.
  Do NOT use scripts/register-outlook-addin.ps1 (heavy ingest COM — crashes some Outlook builds).
#>
param(
    [switch] $Unregister,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",
    [string] $PayloadDir = ""
)

$ErrorActionPreference = "Stop"
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$proj = Join-Path $root "src\Orbit.OutlookLauncher\Orbit.OutlookLauncher.csproj"
$buildDir = Join-Path $root "src\Orbit.OutlookLauncher\bin\$Configuration\net48"
$installDir = Join-Path $env:LOCALAPPDATA "Orbit\OutlookLauncher"
$dll = Join-Path $installDir "Orbit.OutlookLauncher.dll"

$progId = "Orbit.OutlookLauncher.Connect"
$clsid = "{E3C8A1F0-7B2D-4C9E-A6D1-5F8E2B4C9A01}"
$assemblyVersion = "0.1.0.0"
$addinKey = "HKCU:\Software\Microsoft\Office\Outlook\Addins\$progId"
$clsKey = "HKCU:\Software\Classes\CLSID\$clsid"
$progKey = "HKCU:\Software\Classes\$progId"
$lockbackKey = "HKCU:\Software\Classes\Interface\{000C0601-0000-0000-C000-000000000046}"

function Clear-OutlookQuarantine {
    foreach ($ver in @("16.0", "15.0", "14.0")) {
        $root = "HKCU:\Software\Microsoft\Office\$ver\Outlook\Resiliency"
        Remove-Item "$root\CrashingAddinList" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$root\DisabledItems" -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item "$root\StartupItems" -Recurse -Force -ErrorAction SilentlyContinue
        New-Item "$root\DoNotDisableAddinList" -Force | Out-Null
        Set-ItemProperty "$root\DoNotDisableAddinList" -Name $progId -Value 1 -Type DWord
        New-Item "$root\AddinList" -Force | Out-Null
        Set-ItemProperty "$root\AddinList" -Name $progId -Value 1 -Type DWord -ErrorAction SilentlyContinue
        Remove-ItemProperty "HKCU:\Software\Microsoft\Office\$ver\Outlook\AddInLoadTimes" -Name $progId -ErrorAction SilentlyContinue
    }
}

function Unregister-Launcher {
    Write-Host "Unregistering Orbit Outlook launcher..."
    Remove-Item $addinKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $clsKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $progKey -Recurse -Force -ErrorAction SilentlyContinue
    Clear-OutlookQuarantine
    Write-Host "Done. Restart Outlook."
}

if ($Unregister) {
    Unregister-Launcher
    exit 0
}

$outlook = Get-Process OUTLOOK -ErrorAction SilentlyContinue
if ($outlook) {
    throw "Close Classic Outlook before registering (Outlook locks the add-in DLL)."
}

# Prefer the thin launcher over the crashy ingest add-in.
Remove-Item "HKCU:\Software\Microsoft\Office\Outlook\Addins\Orbit.OutlookAddIn.Connect" -Recurse -Force -ErrorAction SilentlyContinue

$sourceDir = $null
if (-not [string]::IsNullOrWhiteSpace($PayloadDir)) {
    $sourceDir = $PayloadDir
    if (-not (Test-Path (Join-Path $sourceDir "Orbit.OutlookLauncher.dll"))) {
        throw "PayloadDir missing Orbit.OutlookLauncher.dll: $sourceDir"
    }
    Write-Host "Using payload: $sourceDir"
}
else {
    Write-Host "Building $proj ($Configuration)..."
    & $dotnet build $proj -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $sourceDir = $buildDir
}

$builtDll = Join-Path $sourceDir "Orbit.OutlookLauncher.dll"
if (-not (Test-Path $builtDll)) {
    throw "DLL missing: $builtDll"
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $sourceDir "*") $installDir -Force
Get-ChildItem -LiteralPath $installDir -Recurse -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

$codeBase = ([Uri]$dll).AbsoluteUri
$asmName = "Orbit.OutlookLauncher, Version=$assemblyVersion, Culture=neutral, PublicKeyToken=null"

# Register for both 64-bit and 32-bit Outlook (Classic Outlook 365 is often 32-bit).
$regViews = @([Microsoft.Win32.RegistryHive]::CurrentUser)
# Use both Wow6432Node and native Classes via .NET for reliability from 64-bit PowerShell:
function Set-ComForView([Microsoft.Win32.RegistryView] $view) {
    $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    $cls = $hive.CreateSubKey("Software\Classes\CLSID\$clsid")
    $cls.SetValue("", "Orbit Outlook Launcher")
    [void]$cls.CreateSubKey("Programmable")
    foreach ($sub in @("InprocServer32", "InprocServer32\$assemblyVersion")) {
        $ip = $hive.CreateSubKey("Software\Classes\CLSID\$clsid\$sub")
        $ip.SetValue("", "mscoree.dll")
        $ip.SetValue("Assembly", $asmName)
        $ip.SetValue("Class", "Orbit.OutlookLauncher.Connect")
        $ip.SetValue("RuntimeVersion", "v4.0.30319")
        $ip.SetValue("ThreadingModel", "Both")
        $ip.SetValue("CodeBase", $codeBase)
    }
    $hive.CreateSubKey("Software\Classes\CLSID\$clsid\Implemented Categories\{B65AD801-ABAF-11D0-BB8B-00A0C90F2744}") | Out-Null
    $pidKey = $hive.CreateSubKey("Software\Classes\CLSID\$clsid\ProgId")
    $pidKey.SetValue("", $progId)
    $prog = $hive.CreateSubKey("Software\Classes\$progId")
    $prog.SetValue("", $progId)
    $prog.CreateSubKey("CLSID").SetValue("", $clsid)
}

if (-not (Test-Path $lockbackKey)) {
    New-Item -Path $lockbackKey -Force | Out-Null
    Set-ItemProperty -Path $lockbackKey -Name "(default)" -Value "Office .NET Framework Lockback Bypass Key"
}

Clear-OutlookQuarantine
Set-ComForView ([Microsoft.Win32.RegistryView]::Registry64)
Set-ComForView ([Microsoft.Win32.RegistryView]::Registry32)

foreach ($addPath in @(
    "Software\Microsoft\Office\Outlook\Addins\$progId",
    "Software\Microsoft\Office\16.0\Outlook\Addins\$progId"
)) {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        $k = $hive.CreateSubKey($addPath)
        $k.SetValue("FriendlyName", "Orbit")
        $k.SetValue("Description", "Send selected mail to the Orbit app (launch only)")
        $k.SetValue("LoadBehavior", 3, [Microsoft.Win32.RegistryValueKind]::DWord)
    }
}

Clear-OutlookQuarantine

Write-Host ""
Write-Host "Registered: $progId (32+64 registry, DLL unblocked)"
Write-Host "Install dir: $installDir"
Write-Host ""
Write-Host "Next:"
Write-Host "  1. Start Orbit once (registers orbit://push-outlook)."
Write-Host "  2. Fully quit Classic Outlook (tray), then start it."
Write-Host "  3. If Outlook says Orbit slows startup, choose Always enable this add-in."
Write-Host "  4. Mail/Home tab -> Send to Orbit."
Write-Host "Or use Settings → Classic Outlook add-in → Install / Update."
Write-Host "Unregister: .\scripts\register-outlook-launcher.ps1 -Unregister"
