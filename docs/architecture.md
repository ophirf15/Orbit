# Orbit architecture

Orbit is a private, single-user Windows work-management application. Capture is instant; structure is inferred continuously; the user approves merges into authoritative state.

## Runtime shape

```text
┌─────────────────┐     ┌──────────────────────┐     ┌─────────────────┐
│   Orbit.App     │────▶│  Orbit.Core.Host     │◀────│     Hermes      │
│  (WinUI shell)  │     │  (authoritative API) │     │ (Docker / LAN)  │
└─────────────────┘     └──────────┬───────────┘     └─────────────────┘
                                   │
                        ┌──────────▼───────────┐
                        │ Orbit.Infrastructure │
                        │ SQLite, FS, Outlook  │
                        └──────────────────────┘
```

- **Orbit Core** owns authoritative data, SQLite, permissions, safe filesystem wrappers, audit, search index, and mutation validation.
- **Hermes** owns reasoning, conversation, suggestions, Telegram continuity, and deciding which typed Orbit capabilities to call.
- Hermes must not receive unrestricted host filesystem access or raw SQLite access.

## Process model

- Primary live database: local SQLite under app-local data (`{LocalDataRoot}/orbit.db`, not inside OneDrive). Versioned migrations via `SqliteMigrator` (ADR 0006).
- Versioned snapshots may sync via a user-selected OneDrive folder (`docs/decisions/0016-onedrive-snapshot-sync.md`).
- External / project folders are **read-only** to Orbit and Hermes, except the Orbit-owned `{projectHome}/.orbit/` sandbox when a primary home is set (`docs/decisions/0021-project-home-orbit-sandbox.md`). New files otherwise go to Orbit-owned generated-output locations.
- UI and Hermes are clients of Core Host (Phase 3). Host exposes loopback HTTP capability API (`docs/decisions/0005-core-host-transport.md`).
- Workbench loads `GET /v1/workbench` and captures via `POST /v1/notes` (`docs/decisions/0007-workbench-capture.md`). Limbo stays on the home surface; detail expands in a side drawer.
- Project folders are attached and indexed read-only (`docs/decisions/0008-external-file-capability.md`). External paths never support delete/rename/move/overwrite through Host.

## Solution projects

| Project | Role |
|---|---|
| `Orbit.App` | WinUI 3 desktop UI |
| `Orbit.Core` | Domain / application logic (no UI) |
| `Orbit.Core.Host` | Background host process |
| `Orbit.Infrastructure` | Persistence, adapters |
| `Orbit.Agent.Contracts` | Typed capability contracts for Hermes |
| `Orbit.Tests` / `Orbit.IntegrationTests` | Tests |

## Related docs

- [Domain model](domain-model.md)
- [Security boundaries](security-boundaries.md)
- [Foundation harvest](foundation-harvest.md)
- [Phases](phases.md)
- [Decisions](decisions/)
