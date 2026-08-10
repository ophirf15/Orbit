# ADR 0009 — Email ingestion via MSG (no Azure)

## Status

Accepted (Phase 7)

## Context

Orbit needs a useful “push this email in” path before Graph, Outlook add-ins, or agent enrichment. Phase 6 left `.msg` in project folders as filename-only index hits. Demo seed already dual-links one email to two projects; ingest must preserve that model.

## Decision

1. **Disk `.msg` is the primary path.** Parse with MSGReader (no Outlook COM required for Explorer drops / Save As files).
2. **Materialize under `{GeneratedFilesRoot}/emails/{id}/`** before/as part of ingest (`original.msg`, body text/HTML, `attachments/`). External project folders stay read-only.
3. **Dedup** by `internet_message_id` when present, else SHA-256 `content_hash` of the `.msg` bytes — re-drop updates rather than cloning rows.
4. **Multi-project** via `email_project_links` only; one artifact, many links. Extractions remain project-scoped later.
5. **Publish `email.ingested`** on EventHub with email id + subject. No LLM in the parser.
6. **Classic Outlook OLE drag** is best-effort. If CF_HDROP / StorageItems is missing, UX documents **Save As `.msg` then drop/browse**.
7. **Classic Outlook COM add-in** (`Orbit.OutlookAddIn`, ADR 0024) is a first-class ingest client: ribbon push → SaveAs `.msg` → same Host ingest. Drag/drop stays fallback.

## Consequences

Agent phases can enrich from real artifacts. CI does not require Outlook. Graph remains out of scope until a later decision revisits cloud mail. The Outlook add-in pairs via Orbit LocalAppData sidecars (no Entra).
