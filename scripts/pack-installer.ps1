#requires -Version 5.1
<#
.SYNOPSIS
  Build a classic Windows installer wizard (Inno Setup) for Orbit.

.DESCRIPTION
  Publishes Orbit.App + Orbit.Core.Host as self-contained win-x64 into one folder,
  then compiles packaging/Orbit.iss into artifacts/installer/Orbit-Setup-<version>.exe.

  This is the takeaway first-install path for a clean PC (wizard UI).
  MSIX + App Installer remains the ADR 0019 update lane.

.PARAMETER Configuration
  Build configuration (default Release).

.PARAMETER Version
  Installer / app version string (default reads Orbit.App.csproj Version, else 0.1.0).

.PARAMETER SkipPublish
  Reuse existing artifacts/installer/publish without republishing.

.PARAMETER InnoSetupPath
  Optional path to ISCC.exe. If omitted, searches common install locations / winget.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [string] $Version = "",

    [switch] $SkipPublish,

    [string] $InnoSetupPath = ""
)

$ErrorActionPreference = "Stop"

$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

function Get-AppVersion {
    param([string] $Fallback)
    $csproj = Join-Path $root "src\Orbit.App\Orbit.App.csproj"
    $text = Get-Content $csproj -Raw
    if ($text -match '<Version>([^<]+)</Version>') {
        return $Matches[1].Trim()
    }
    return $Fallback
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AppVersion -Fallback "0.1.0"
}

$outRoot = Join-Path $root "artifacts\installer"
$publishDir = Join-Path $outRoot "publish"
$iss = Join-Path $root "packaging\Orbit.iss"

New-Item -ItemType Directory -Force -Path $outRoot | Out-Null

function Find-Iscc {
    param([string] $Explicit)
    if (-not [string]::IsNullOrWhiteSpace($Explicit) -and (Test-Path $Explicit)) {
        return (Resolve-Path $Explicit).Path
    }

    $pf86 = ${env:ProgramFiles(x86)}
    $candidates = @(
        (Join-Path $pf86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $pf86 "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            return $c
        }
    }

    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    return $null
}

$iscc = Find-Iscc -Explicit $InnoSetupPath
if (-not $iscc) {
    Write-Host "Inno Setup not found - installing JRSoftware.InnoSetup via winget..."
    winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
    $iscc = Find-Iscc -Explicit ""
}
if (-not $iscc) {
    throw "ISCC.exe not found after Inno Setup install. Install from https://jrsoftware.org/isinfo.php and re-run."
}

Write-Host "Using ISCC: $iscc"
Write-Host "Version: $Version"

if (-not $SkipPublish) {
    if (Test-Path $publishDir) {
        Remove-Item -Recurse -Force $publishDir
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    $appProject = Join-Path $root "src\Orbit.App\Orbit.App.csproj"
    $hostProject = Join-Path $root "src\Orbit.Core.Host\Orbit.Core.Host.csproj"

    Write-Host "Publishing Orbit.App (self-contained win-x64)..."
    & $dotnet @(
        "publish", $appProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:WindowsPackageType=None",
        "-p:WindowsAppSDKSelfContained=true",
        "-p:PublishTrimmed=false",
        "-p:PublishReadyToRun=true",
        # Bake release version into assembly so in-app updater compares correctly.
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-o", $publishDir
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Orbit.App publish failed with exit $LASTEXITCODE"
    }

    Write-Host "Publishing Orbit.Core.Host into the same folder..."
    & $dotnet @(
        "publish", $hostProject,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishTrimmed=false",
        "-p:PublishReadyToRun=true",
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-o", $publishDir
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Orbit.Core.Host publish failed with exit $LASTEXITCODE"
    }

    $mcpProject = Join-Path $root "src\Orbit.Mcp\Orbit.Mcp.csproj"
    $mcpOut = Join-Path $publishDir "orbit-mcp"
    Write-Host "Publishing Orbit.Mcp (self-contained win-x64) into publish\orbit-mcp..."
    if (Test-Path $mcpOut) {
        Remove-Item -Recurse -Force $mcpOut
    }
    New-Item -ItemType Directory -Force -Path $mcpOut | Out-Null
    & $dotnet @(
        "publish", $mcpProject,
        "-c", $Configuration,
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishTrimmed=false",
        "-p:Version=$Version",
        "-p:InformationalVersion=$Version",
        "-o", $mcpOut
    )
    if ($LASTEXITCODE -ne 0) {
        throw "Orbit.Mcp publish failed with exit $LASTEXITCODE"
    }

    $appExe = Join-Path $publishDir "Orbit.App.exe"
    $hostExe = Join-Path $publishDir "Orbit.Core.Host.exe"
    $mcpExe = Join-Path $mcpOut "Orbit.Mcp.exe"
    $mcpDll = Join-Path $mcpOut "Orbit.Mcp.dll"
    if (-not (Test-Path $appExe)) { throw "Missing $appExe after publish" }
    if (-not (Test-Path $hostExe)) { throw "Missing $hostExe after publish" }
    if (-not (Test-Path $mcpExe)) { throw "Missing $mcpExe after publish (Hermes MCP bridge)" }
    if (-not (Test-Path $mcpDll)) { throw "Missing $mcpDll after publish (Hermes MCP bridge)" }

    $hermesDocs = Join-Path $publishDir "docs\hermes"
    if (-not (Test-Path (Join-Path $hermesDocs "SOUL.md"))) {
        throw "Packaged Hermes docs missing at $hermesDocs (SOUL.md). Orbit.App must publish docs/hermes for Connect Hermes."
    }
    $skillDirs = @(Get-ChildItem (Join-Path $hermesDocs "skills\orbit") -Directory -ErrorAction SilentlyContinue)
    if ($skillDirs.Count -lt 6) {
        throw "Expected >=6 Orbit skills under docs/hermes/skills/orbit; found $($skillDirs.Count)."
    }

    @(
        "Orbit install layout",
        "====================",
        "Orbit.App.exe          - WinUI shell",
        "Orbit.Core.Host.exe    - local API (started by the app)",
        "orbit-mcp\             - Orbit.Mcp.exe (Hermes MCP stdio bridge → Core)",
        "docs/hermes/           - SOUL, skills, cron/webhook manifests (Connect Hermes)",
        "",
        "Data lives under %LocalAppData%\Orbit\ after first launch.",
        "Connect Hermes copies orbit-mcp into %LocalAppData%\Orbit\orbit-mcp\ and wires Hermes config.",
        "Hermes (agent) is optional and separate - see Settings > Hermes after install.",
        "After install: Settings → Connect Hermes so skills + MCP sync into HERMES_HOME."
    ) | Set-Content -Path (Join-Path $publishDir "INSTALL-README.txt") -Encoding utf8
}

Write-Host "Compiling installer wizard..."
# Inno #defines treat backslash as escape - use forward slashes.
$publishDirIss = ($publishDir -replace '\\', '/')
$outDirIss = ($outRoot -replace '\\', '/')
& $iscc @(
    "/DMyAppVersion=$Version",
    "/DPublishDir=$publishDirIss",
    "/DOutputDir=$outDirIss",
    $iss
)
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit $LASTEXITCODE"
}

$setup = Get-ChildItem $outRoot -Filter "Orbit-Setup-*.exe" | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if (-not $setup) {
    throw "Installer exe not found under $outRoot"
}

$hash = (Get-FileHash -Algorithm SHA256 $setup.FullName).Hash
$hashFile = Join-Path $outRoot ($setup.Name + ".sha256")
Set-Content -Path $hashFile -Value "$hash  $($setup.Name)" -Encoding ascii

Write-Host ""
Write-Host "Installer ready:"
Write-Host "  $($setup.FullName)"
Write-Host "  SHA256: $hash"
Write-Host "Copy that .exe to another PC and run it (admin wizard)."
