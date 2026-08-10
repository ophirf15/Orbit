# ADR 0018 — Global search and evidence-backed answers

## Status

Accepted (Phase 16)

## Context

Users often remember fragments (a name, “W-9”, “Harbor Court status”) rather than where the fact lives. Orbit already had `search_documents` + FTS5 for files and graph rows, plus `ContextBundleService` for project-scoped packs, but `/v1/search` was a stub and emails/calendar/conversations were not uniformly indexed.

## Decision

1. **`SearchIndexRebuilder` owns the unified projection** — projects, workstreams, tasks, blockers, notes, people, orgs, emails, files, calendar events, conversations, and messages into `search_documents` / FTS5.
2. **`GlobalSearchService`** answers fragmentary queries via FTS (LIKE fallback). Ranking combines match quality, recency, attention scores, and optional `focusProjectId` / `focusMeetingId` boosts without requiring a current-project filter.
3. **`EvidenceService` returns structured JSON**, not LLM synthesis: EIN/W-9 templates resolve org + provenance fact + linked W-9 file; project status uses `ContextBundleService` so Harbor Court answers never include Riverview extractions.
4. **Agent tools** `orbit_search` and `orbit_answer_with_evidence` mirror the HTTP APIs. Search UI reuses `FilePreviewControl` for file/email previews.

## Consequences

Demo seed includes Acme Holdings EIN + on-disk W-9 for AC. Full fuzzy Levenshtein and LLM narrative synthesis stay out of scope; prefix/FTS + structured packs satisfy Phase 16.
