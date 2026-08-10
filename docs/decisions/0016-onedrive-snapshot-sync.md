# ADR 0016 — OneDrive snapshot sync (not live SQLite)

## Status

Accepted (Phase 14)

## Context

Users need continuity across company machines that already sync via OneDrive. Placing a live SQLite (WAL) database inside a synchronized folder causes corruption and lock fights. Orbit must never require OneDrive OAuth for v1.

## Decision

1. **Live DB stays under LocalAppData** (`LocalDataRoot/orbit.db`). OneDrive only receives versioned snapshots.
2. **`SnapshotService`** uses `SqliteConnection.BackupDatabase` into `{syncFolder}/OrbitSnapshots/{snapshotId}/orbit.db` plus `manifest.json` (id, schemaVersion, revision, parentRevision, deviceId/name, createdAt, sha256).
3. **`DeviceId`** is generated once in settings and persisted; manifests carry device identity for lineage.
4. **Local lineage** lives in `sync-lineage.json` beside the live DB (revision/parent/dirty/conflict), never as the sole copy of truth in the cloud.
5. **Startup reconcile** compares local vs latest valid cloud snapshot: cloud-ahead + local not diverged → safe restore (after last-known-good copy); local-ahead → continue; both diverged → **conflict**, no silent overwrite.
6. **Host APIs** expose snapshot/list/restore/status so the App does not touch DB files directly. Debounced hosted service + graceful shutdown snapshot when the folder is configured.
7. **Capture never blocks** on missing/offline sync folder.

## Consequences

CI proves snapshot, empty-machine restore, divergent conflict, corrupt rejection, and older restore with temp folders. Users must pick a OneDrive-synced path in Settings (manual TODO). Real-time multi-writer merge and email binary blob sync remain out of scope.
