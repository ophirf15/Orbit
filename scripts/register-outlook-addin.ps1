#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and registers the Orbit Classic Outlook COM add-in for the current user.

.NOTES
  1. Close Outlook before running.
  2. Start Orbit (Core Host) after install so Add to Orbit can ingest.
  3. In Outlook: File -> Options -> Add-ins -> COM Add-ins -> ensure "Orbit" is checked.
#>
param(
    [switch] $Unregister,
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$proj = Join-Path $root "src\Orbit.OutlookAddIn\Orbit.OutlookAddIn.csproj"
$buildDir = Join-Path $root "src\Orbit.OutlookAddIn\bin\$Configuration\net48"
$installDir = Join-Path $env:LOCALAPPDATA "Orbit\OutlookAddIn"
$dll = Join-Path $installDir "Orbit.OutlookAddIn.dll"

$progId = "Orbit.OutlookAddIn.Connect"
$clsid = "{B7E6C2A1-4F3D-4E8A-9C1B-0D2E3F4A5B6C}"
$assemblyVersion = "0.1.0.0"
$addinKey = "HKCU:\Software\Microsoft\Office\Outlook\Addins\$progId"
$clsKey = "HKCU:\Software\Classes\CLSID\$clsid"
$progKey = "HKCU:\Software\Classes\$progId"
$lockbackKey = "HKCU:\Software\Classes\Interface\{000C0601-0000-0000-C000-000000000046}"

function Clear-OutlookQuarantine {
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\CrashingAddinList" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DisabledItems" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Addins\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\Software\Microsoft\Office\Outlook\AddinsData\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    New-Item "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList" -Force | Out-Null
    Set-ItemProperty "HKCU:\Software\Microsoft\Office\16.0\Outlook\Resiliency\DoNotDisableAddinList" -Name $progId -Value 1 -Type DWord
}

function Unregister-OrbitAddIn {
    Write-Host "Unregistering Orbit Outlook add-in..."
    Remove-Item $addinKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $clsKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $progKey -Recurse -Force -ErrorAction SilentlyContinue
    Clear-OutlookQuarantine
    Write-Host "Done. Restart Outlook."
}

if ($Unregister) {
    Unregister-OrbitAddIn
    exit 0
}

$outlook = Get-Process OUTLOOK -ErrorAction SilentlyContinue
if ($outlook) {
    throw "Close Classic Outlook before registering (Outlook locks the add-in DLL)."
}

Write-Host "Building $proj ($Configuration)..."
& $dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$builtDll = Join-Path $buildDir "Orbit.OutlookAddIn.dll"
if (-not (Test-Path $builtDll)) {
    throw "Build succeeded but DLL missing: $builtDll"
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item (Join-Path $buildDir "*") $installDir -Force
if (-not (Test-Path $dll)) {
    throw "Install copy failed: $dll"
}

$codeBase = ([Uri]$dll).AbsoluteUri
$asmName = "Orbit.OutlookAddIn, Version=$assemblyVersion, Culture=neutral, PublicKeyToken=null"

if (-not (Test-Path $lockbackKey)) {
    New-Item -Path $lockbackKey -Force | Out-Null
    Set-ItemProperty -Path $lockbackKey -Name "(default)" -Value "Office .NET Framework Lockback Bypass Key"
    Write-Host "Wrote Office CLR lockback bypass key."
}

Clear-OutlookQuarantine

Remove-Item $clsKey -Recurse -Force -ErrorAction SilentlyContinue
New-Item -Path $clsKey -Force | Out-Null
Set-ItemProperty -Path $clsKey -Name "(default)" -Value "Orbit Outlook Add-in"
New-Item -Path "$clsKey\Programmable" -Force | Out-Null

function Set-NetComInproc([string] $keyPath) {
    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name "(default)" -Value "mscoree.dll"
    Set-ItemProperty -Path $keyPath -Name "Assembly" -Value $asmName
    Set-ItemProperty -Path $keyPath -Name "Class" -Value "Orbit.OutlookAddIn.Connect"
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
Set-ItemProperty -Path $progKey -Name "(default)" -Value "Orbit.OutlookAddIn.Connect"
New-Item -Path "$progKey\CLSID" -Force | Out-Null
Set-ItemProperty -Path "$progKey\CLSID" -Name "(default)" -Value $clsid

New-Item -Path $addinKey -Force | Out-Null
Set-ItemProperty -Path $addinKey -Name "FriendlyName" -Value "Orbit"
Set-ItemProperty -Path $addinKey -Name "Description" -Value "Push Classic Outlook mail into Orbit"
Set-ItemProperty -Path $addinKey -Name "LoadBehavior" -Value 3 -Type DWord

Write-Host ""
Write-Host "Registered: $progId"
Write-Host "Install dir: $installDir"
Write-Host "CodeBase: $codeBase"
Write-Host ""
Write-Host "Next:"
Write-Host "  1. Start Orbit (Core Host must be running)."
Write-Host "  2. Start Classic Outlook."
Write-Host "  3. Look for the Orbit ribbon tab -> Add to Orbit."
Write-Host "  4. If missing: File -> Options -> Add-ins -> COM Add-ins -> check Orbit."
Write-Host "  5. Diagnostics: %LocalAppData%\Orbit\outlook-addin.log"
Write-Host ""
Write-Host "Unregister later:"
Write-Host "  .\scripts\register-outlook-addin.ps1 -Unregister"
