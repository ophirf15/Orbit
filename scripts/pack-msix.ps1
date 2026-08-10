#requires -Version 5.1
<#
.SYNOPSIS
  Best-effort MSIX pack for Orbit.App (unsigned or secret-backed signing).

.DESCRIPTION
  Keeps the daily unpackaged loop untouched (WindowsPackageType=None in csproj).
  This script publishes with packaging properties for release artifacts only.

.PARAMETER Configuration
  Build configuration (default Release).

.PARAMETER OutputDir
  Directory for artifacts (default artifacts/msix).

.PARAMETER SkipSign
  Force AppxPackageSigningEnabled=false (default when cert secrets are absent).
#>
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $OutputDir = "",

    [switch] $SkipSign
)

$ErrorActionPreference = "Stop"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "artifacts\msix"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$certPath = $env:ORBIT_SIGNING_CERT_PATH
$certPassword = $env:ORBIT_SIGNING_CERT_PASSWORD
$signingEnabled = -not $SkipSign -and -not [string]::IsNullOrWhiteSpace($certPath) -and (Test-Path $certPath)

$project = Join-Path $root "src\Orbit.App\Orbit.App.csproj"
$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-p:Platform=x64",
    "-p:RuntimeIdentifier=win-x64",
    "-p:WindowsPackageType=MSIX",
    "-p:GenerateAppxPackageOnBuild=true",
    "-p:AppxPackageDir=$OutputDir\",
    "-p:AppxBundle=Never",
    "--self-contained", "true"
)

if ($signingEnabled) {
    Write-Host "Signing enabled via ORBIT_SIGNING_CERT_PATH"
    $publishArgs += "-p:AppxPackageSigningEnabled=true"
    $publishArgs += "-p:PackageCertificateKeyFile=$certPath"
    if (-not [string]::IsNullOrWhiteSpace($certPassword)) {
        $publishArgs += "-p:PackageCertificatePassword=$certPassword"
    }
}
else {
    Write-Host "Packing unsigned (set ORBIT_SIGNING_CERT_PATH or pass secrets in CI to sign)."
    $publishArgs += "-p:AppxPackageSigningEnabled=false"
}

Write-Host "Using: $dotnet"
& $dotnet @publishArgs
$exit = $LASTEXITCODE

# Copy appinstaller template for release upload convenience.
$template = Join-Path $root "packaging\Orbit.appinstaller"
if (Test-Path $template) {
    Copy-Item -Force $template (Join-Path $OutputDir "Orbit.appinstaller")
}

if ($exit -ne 0) {
    Write-Warning "MSIX publish exited $exit. Unpackaged Debug/Release builds remain the supported daily loop."
    exit $exit
}

Write-Host "Artifacts under: $OutputDir"
Get-ChildItem -Recurse $OutputDir -ErrorAction SilentlyContinue | Select-Object -First 40 FullName
