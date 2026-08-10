# Security boundaries

## Orbit-owned vs external

| Surface | Orbit / Hermes |
|---|---|
| Orbit SQLite, settings, generated output | Full CRUD via Core capabilities |
| External / project / Windows user files | **Read-only** — no delete, overwrite, rename, move, or modify |
| Installed app binaries | Never edit in place |

Enforcement must live below the model/tool layer, not only in prompts.

## Capability security

- Hermes calls typed Orbit capabilities only (see `Orbit.Agent.Contracts`).
- Hermes never writes SQLite directly.
- Ambiguous / destructive / high-impact operations require confirmation.
- Every agent mutation is audited.

## Local API (Phase 3+)

- Loopback by default (`127.0.0.1:8741`); optional trusted LAN bind via `coreHostBindAddress`
- Bearer / API key mandatory when not loopback (Host refuses to start without key sidecar)
- When a Core Host API key sidecar exists, Bearer is required even on loopback
- No public wildcard exposure by default; no permissive browser CORS
- Capability endpoints only — no raw SQL, shell, or arbitrary path writes
- Filesystem writes allowed only under Orbit `generatedFilesRoot` (enforced in Host)
- Filesystem reads for project content limited to attached `project_folders` roots (Phase 6); see ADR 0008
- External mutation routes (`/v1/files/external/*`) always return 403

## Secrets

- Never commit API keys or settings with secret material
- Hermes API key lives in a LocalAppData sidecar referenced by settings.json
- Foundation is development-time only — **zero runtime dependency**

## Evidence

Agent answers that claim facts should surface supporting sources (files, emails, tasks) rather than opaque model assertions.
