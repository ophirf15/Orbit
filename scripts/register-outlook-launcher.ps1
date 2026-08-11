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
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\CrashingAddinList" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DisabledItems" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Addins\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    New-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList" -Force | Out-Null
    Set-ItemProperty "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList" -Name $progId -Value 1 -Type DWord
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

$codeBase = ([Uri]$dll).AbsoluteUri
$asmName = "Orbit.OutlookLauncher, Version=$assemblyVersion, Culture=neutral, PublicKeyToken=null"

if (-not (Test-Path $lockbackKey)) {
    New-Item -Path $lockbackKey -Force | Out-Null
    Set-ItemProperty -Path $lockbackKey -Name "(default)" -Value "Office .NET Framework Lockback Bypass Key"
}

Clear-OutlookQuarantine

Remove-Item $clsKey -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $clsKey -Force | Out-Null
Set-ItemProperty -Path $clsKey -Name "(default)" -Value "Orbit Outlook Launcher"
New-Item -Path "$clsKey\Programmable" -Force | Out-Null

function Set-NetComInproc([string] $keyPath) {
    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name "(default)" -Value "mscoree.dll"
    Set-ItemProperty -Path $keyPath -Name "Assembly" -Value $asmName
    Set-ItemProperty -Path $keyPath -Name "Class" -Value "Orbit.OutlookLauncher.Connect"
    Set-ItemProperty -Path $keyPath -Name "RuntimeVersion" -Value "v4.0.30319"
    Set-ItemProperty -Path $keyPath -Name "ThreadingModel" -Value "Both"
    Set-ItemProperty -Path $keyPath -Name "CodeBase" -Value $codeBase
}

Set-NetComInproc "$clsKey\InprocServer32"
Set-NetComInproc "$clsKey\InprocServer32\$assemblyVersion"
New-Item -Path "$clsKey\Implemented Categories\{B65AD801-ABAF-11D0-BB8B-00A0C90F2744}" -Force | Out-Null
New-Item -Path "$clsKey\ProgId" -Force | Out-Null
Set-ItemProperty -Path "$clsKey\ProgId" -Name "(default)" -Value $progId

New-Item -Path $progKey -Force | Out-Null
Set-ItemProperty -Path $progKey -Name "(default)" -Value "Orbit.OutlookLauncher.Connect"
New-Item -Path "$progKey\CLSID" -Force | Out-Null
Set-ItemProperty -Path "$progKey\CLSID" -Name "(default)" -Value $clsid

New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name "FriendlyName" -Value "Orbit"
Set-ItemProperty -Path $addinKey -Name "Description" -Value "Send selected mail to the Orbit app (launch only)"
Set-ItemProperty -Path $addinKey -Name "LoadBehavior" -Value 3 -Type DWord

Write-Host ""
Write-Host "Registered: $progId"
Write-Host "Install dir: $installDir"
Write-Host ""
Write-Host "Next:"
Write-Host "  1. Start Orbit once (registers orbit://push-outlook)."
Write-Host "  2. Start Classic Outlook."
Write-Host "  3. Mail/Home tab -> Send to Orbit."
Write-Host "Or use Settings → Classic Outlook add-in → Install / Update."
Write-Host "Unregister: .\scripts\register-outlook-launcher.ps1 -Unregister"
