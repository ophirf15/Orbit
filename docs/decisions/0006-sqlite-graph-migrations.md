# ADR 0006 — SQLite domain graph and migrations

## Status

Accepted (Phase 4)

## Context

Orbit needs durable local state with context-aware relationships so shared vendors (e.g. MetroFiber) do not collapse tasks across properties. Production must not use `EnsureCreated()`.

## Decision

- Store DB at `{LocalDataRoot}/orbit.db` via `Microsoft.Data.Sqlite`
- Versioned embedded SQL migrations + `schema_migrations` table
- Backup file `orbit.db.bak-{timestamp}` before migrations whose version name contains `destructive`
- GUID (`D` format) TEXT primary keys
- Polymorphic `relationships` edges with optional `project_id` / `workstream_id` / `task_id` context
- Rebuildable `search_documents` + FTS5 mirror
- Demo seed (`--seed-demo` / tests) for Harbor Court + Riverview + MetroFiber

## Consequences

Host migrates on startup. Phase 5+ write capabilities through Host against this schema. EF Core is not introduced.
