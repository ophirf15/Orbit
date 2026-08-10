# Orbit

Private, single-user Windows work-management workbench for property onboarding.  
**Capture first. Organize continuously. Approve merges, not thinking.**

## Prerequisites

- Windows 10 1809+ / Windows 11
- [Developer Mode](ms-settings:developers) enabled
- [.NET 9 SDK](https://dotnet.microsoft.com/download) (x64) — use `C:\Program Files\dotnet\dotnet.exe` if an x86 host shadows PATH
- Windows App SDK / WinUI workloads (templates: `dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates`)

## Build

```powershell
.\build.ps1
.\build.ps1 -Test
```

Or:

```powershell
dotnet build Orbit.sln -c Debug
dotnet test Orbit.sln -c Debug --filter "FullyQualifiedName!~Orbit.App"
```

## Run UI (unpackaged)

```powershell
dotnet run --project src/Orbit.App/Orbit.App.csproj -c Debug
```

## Run Core Host stub

```powershell
dotnet run --project src/Orbit.Core.Host/Orbit.Core.Host.csproj
```

## Settings

Stored under `%LocalAppData%\Orbit\settings.json`.  
Hermes API key (if any) lives in a sidecar file referenced by settings — never commit secrets. See `docs/settings.example.json`.

## Docs

- [Architecture](docs/architecture.md)
- [Domain model](docs/domain-model.md)
- [Security boundaries](docs/security-boundaries.md)
- [Foundation harvest](docs/foundation-harvest.md)
- [Phases](docs/phases.md)
- [Phase 1 plan](docs/plans/2026-08-06-001-phase1-foundation-repo-scaffold-plan.md)

## Foundation

Foundation is a **development-time** harvest library only. Orbit has **zero** runtime dependency on Foundation.
