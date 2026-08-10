# ADR 0012 — Live agent suggestions and typed mutation tools

## Status

Accepted (Phase 10)

## Context

Hermes (and the Workbench) need ambient suggestions after meaningful graph events, plus safe typed CRUD over Orbit-owned state. Suggestions must be accept/reject gated when applying inferred structure. External filesystem mutate, arbitrary SQL, and shell must remain impossible. Live Hermes is not required to generate suggestions for acceptance criteria.

## Decision

1. **`agent_suggestions`** (schema from 0001) is the store of record. Statuses: `pending` / `accepted` / `rejected` / `expired`. Explanation and evidence live in `payload_json` (no migration required).
2. **`AgentEventWorker`** (Host `BackgroundService`) subscribes to `EventHub`, debounces (~750ms), and runs **`SuggestionEngine`** heuristics on `note.created` limbo captures: project name/code match → `assign_to_project`; else `review_limbo`. Publishes `suggestion.created`.
3. **Accept** of `assign_to_project` sets `notes.project_id`, clears limbo (`is_limbo=0`), creates a task from the note text, and **never** rewrites `original_text`. Reject only flips status. Both write `audit_events`.
4. **Host APIs**: `GET /v1/suggestions?status=pending`, `POST .../accept`, `POST .../reject`. Mutation tools under `/v1/agent/tools/` (`orbit_create_task`, `orbit_update_task`, `orbit_create_note`, `orbit_link_entities`, `orbit_update_contact`, `orbit_accept_suggestion`, `orbit_set_blocker`) validate in Core and audit.
5. **Workbench** limbo/drawer expose Accept/Reject and refresh after decisions. External file mutate routes stay 403.

## Consequences

Ambient suggestions work without Hermes. Hermes can call the same Host tools with the Core Bearer key. Product choice of auto-merge vs always-confirm remains deferred in `docs/TODO.md`. Email `link_contact` heuristics and live Hermes tool registration are follow-ups.
