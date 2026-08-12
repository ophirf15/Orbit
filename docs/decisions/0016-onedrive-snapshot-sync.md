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

CI proves snapshot, empty-machine continue/restore, divergent conflict, corrupt rejection, and older restore with temp folders. Users pick a OneDrive-synced path in Settings via folder picker; App validates writability. Empty local + existing snapshots surfaces a shell **Continue from OneDrive backup** choice (no silent overwrite); restore reuses `RestoreSnapshot` / sync restore API. Real-time multi-writer merge and email binary blob sync remain out of scope.

## UX note (Phase 3 / 2026-08-12)

- Settings shows resolved folder path, device id, last snapshot time, conflict state, and auto-backup hint (hosted quiet-period).
- Host reconcile does **not** auto-restore onto an empty local DB; it sets `continueFromBackupAvailable` so the App can prompt.
- Safe auto-restore still applies for non-empty local when cloud is ahead and local lineage is a clean ancestor (unchanged ADR rule).
