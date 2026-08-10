# ADR 0008 — External file capability (read-only)

## Status

Accepted (Phase 6)

## Context

Orbit must index and preview project folders without ever mutating user files. Phase 3 left `TryResolveReadable` too broad. The constitution requires a capability layer that cannot delete, rename, move, or overwrite external files.

## Decision

1. Persist attached roots in `project_folders`. Readable Host paths must resolve under an attached root or the generated-files root.
2. `IExternalFileCapability` exposes only List, Stat, OpenRead, ReadTextPreview, and OpenExternally. No write/delete/rename/move members. Host also maps explicit `/v1/files/external/*` mutation routes to 403.
3. Index into rebuildable `file_artifacts` + `search_documents` (FTS when available). Content extraction is best-effort: TXT/CSV, PDF (PdfPig), DOCX/XLSX (Open XML); images metadata-only; MSG filename-only until Phase 7.
4. Debounced `FileSystemWatcher` per folder (~750ms) triggers folder reindex; cloud/placeholder IO soft-fails to `offline_placeholder`.

## Consequences

Hermes never receives raw filesystem handles. Generated output remains the primary writable surface; project homes may also expose a dedicated `{home}/.orbit` sandbox (ADR 0021). Richer preview/OCR and MSG parsing arrive in later phases.

## Amendment

See [ADR 0021](0021-project-home-orbit-sandbox.md) for primary home folders and the `.orbit` writable island.
