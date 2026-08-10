#requires -Version 5.1
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Debug",

    [switch] $Test,

    [switch] $NoRestore
)

$ErrorActionPreference = "Stop"

# Prefer x64 SDK when x86 dotnet.exe shadows PATH.
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$buildArgs = @("build", "Orbit.sln", "-c", $Configuration, "-p:Platform=x64")
if ($NoRestore) {
    $buildArgs += "--no-restore"
}

Write-Host "Using: $dotnet"
& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($Test) {
    & $dotnet test "Orbit.sln" -c $Configuration --no-build -p:Platform=x64
    exit $LASTEXITCODE
}
