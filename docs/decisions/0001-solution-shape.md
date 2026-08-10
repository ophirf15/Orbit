# ADR 0001 — Seven-project solution shape

## Status

Accepted (Phase 1)

## Context

Orbit needs a WinUI UI, a persistent Core Host, infrastructure adapters, and Hermes-facing contracts without letting the UI own SQLite or raw filesystem mutation.

## Decision

Use seven projects:

- `Orbit.App` — WinUI 3
- `Orbit.Core` — domain (no UI)
- `Orbit.Core.Host` — background host
- `Orbit.Infrastructure` — SQLite/FS/adapters
- `Orbit.Agent.Contracts` — Hermes DTOs/capabilities
- `Orbit.Tests` / `Orbit.IntegrationTests`

Dependency direction: App/Host → Core + Infrastructure + Contracts; Infrastructure → Core; Contracts → Core; never Core → App.

## Consequences

Phase 1 Host is a stub. Real IPC arrives in Phase 3. Clean boundaries make safety tests enforceable.
