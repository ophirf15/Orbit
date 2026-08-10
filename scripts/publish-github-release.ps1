#requires -Version 5.1
<#
.SYNOPSIS
  Build Orbit-Setup and publish it as a GitHub Release asset (takeaway update lane).

.DESCRIPTION
  1. Optional version bump is your responsibility in Orbit.App.csproj before running.
  2. Runs pack-installer.ps1
  3. Creates (or updates) a GitHub release for tag v<Version> and uploads Orbit-Setup-*.exe

  Requires: gh CLI authenticated (`gh auth login`), git clean enough to tag.

.PARAMETER Version
  Release version without leading v (default: Version from Orbit.App.csproj).

.PARAMETER SkipPack
  Reuse existing artifacts/installer output.

.PARAMETER Draft
  Create the GitHub release as a draft.
#>
param(
    [string] $Version = "",
    [switch] $SkipPack,
    [switch] $Draft
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

function Get-AppVersion {
    $csproj = Join-Path $root "src\Orbit.App\Orbit.App.csproj"
    $text = Get-Content $csproj -Raw
    if ($text -match '<Version>([^<]+)</Version>') {
        return $Matches[1].Trim()
    }
    throw "Could not read <Version> from Orbit.App.csproj"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-AppVersion
}

$tag = "v$Version"
if ($Version -match '^v') {
    $tag = $Version
    $Version = $Version.TrimStart('v', 'V')
    $tag = "v$Version"
}

$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    throw "GitHub CLI (gh) not found. Install from https://cli.github.com/ and run gh auth login."
}

if (-not $SkipPack) {
    & (Join-Path $root "scripts\pack-installer.ps1") -Configuration Release -Version $Version
}

$setup = Get-ChildItem (Join-Path $root "artifacts\installer") -Filter "Orbit-Setup-$Version.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $setup) {
    $setup = Get-ChildItem (Join-Path $root "artifacts\installer") -Filter "Orbit-Setup-*.exe" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}
if (-not $setup) {
    throw "No Orbit-Setup-*.exe under artifacts/installer. Run without -SkipPack."
}

$sha = Join-Path $setup.DirectoryName ($setup.Name + ".sha256")
$files = @($setup.FullName)
if (Test-Path $sha) {
    $files += $sha
}

Write-Host "Ensuring git tag $tag exists locally..."
$existing = git rev-parse -q --verify "refs/tags/$tag" 2>$null
if (-not $existing) {
    git tag -a $tag -m "Orbit $Version"
    Write-Host "Created tag $tag (push with: git push origin $tag)"
}

$draftArgs = @()
if ($Draft) {
    $draftArgs += "--draft"
}

$releaseExists = $false
gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    $releaseExists = $true
}

if ($releaseExists) {
    Write-Host "Uploading assets to existing release $tag..."
    gh release upload $tag @files --clobber
}
else {
    Write-Host "Creating GitHub release $tag..."
    gh release create $tag @files @draftArgs --generate-notes --title "Orbit $Version"
}

Write-Host ""
Write-Host "Release ready: https://github.com/ophirf15/Orbit/releases/tag/$tag"
Write-Host "On another PC: Settings → Check now → Install update (downloads this setup, silent in-place upgrade)."
