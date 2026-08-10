# ADR 0014 — Multi-mailbox calendar intelligence

## Status

Accepted (Phase 12)

## Context

Orbit needs upcoming meeting context from Classic Outlook (multiple mailboxes) without mandatory Azure Graph consent. Attention for related work must rise when a meeting is imminent, without silently rewriting user Priority. Tests and CI cannot depend on Outlook COM.

## Decision

1. **`ICalendarProvider`** normalizes all providers into `calendar_sources` / `calendar_events` / `event_entity_links`. Domain model does not change when swapping providers.
2. **Provider A — Outlook COM** (`OutlookCalendarProvider`): late-bound, read-only, best-effort. Missing Outlook / inaccessible stores return `Available=false` with a status message — never throw to callers.
3. **Provider B — ICS** (`IcsCalendarProvider`): file path or HTTP(S) URL. Required for tests and the Settings sync path.
4. **Provider C — Graph** (`GraphCalendarProvider`): stub only; documents optional future MSAL/public-client work. Not mandatory.
5. **`CalendarSyncService`** upserts sources (preserving mailbox/calendar identity) and events by stable external keys; then **`MeetingProjectLinker`** (subject/body/location contains project name/code → link + confidence/provenance) and **`AttentionScorer`** (imminence + optional open-blocker bump on `calendar_events.attention_score` only).
6. **Never mutate `tasks.priority` / `workstreams.priority`** from calendar scoring.
7. Host: `GET /v1/calendar/context`, `POST /v1/calendar/sync`, `GET /v1/calendar/sources`, `POST /v1/calendar/sources/subscribe`; agent tool `orbit_get_calendar_context`; `ContextBundle.meetings` filled from linked events.

## Consequences

CI proves ICS import, multi-source distinguishability, Harbor Court attention without Priority rewrite, and provider swappability. Live Outlook discovery remains an environment verify item when COM is unavailable.
