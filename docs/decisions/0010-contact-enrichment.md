# ADR 0010 — Contact enrichment from email (heuristic)

## Status

Accepted (Phase 8)

## Context

Phase 7 stores email artifacts and participants. Contacts still needed people/org upserts with provenance, safe dedupe, and an audited mutation path Hermes can call later. Full signature LLM belongs with Hermes (Phase 9–10).

## Decision

1. **Enrich after ingest** inside `EmailIngestionService` via `EmailContactEnricher` (sync heuristics only). Keep `email.ingested`; also publish `contact.observed` when people were touched.
2. **Exact email = same person.** Normalized phone can attach facts to an existing person. Never merge on display name alone. Same name + same org domain + different emails → `agent_suggestions` type `contact_merge`.
3. **Provenance** lives in `contact_fact_provenance` (migration `0004_contact_enrichment.sql`): entity, field, value, source email, source kind (`email_participant`, `signature_heuristic`, `user_update`, `domain_inference`).
4. **Org-by-domain** creates/finds organizations (skip free-mail domains) and memberships; signature title/phones apply to the From person.
5. **`UpdateContact`** is Host-only (`POST`/`PATCH /v1/contacts/{id}`) with patch + provenance + requestedBy; writes methods/memberships and an `audit_events` row. App never talks to SQLite for contacts.
6. **People are global**; multi-project participation uses `relationships` (`involved_in`), not cloned person rows.

## Consequences

Contact detail and People UI can show methods, org/title, projects, recent emails, and provenance. Hermes signature parsing can replace or augment `SignatureHeuristic` later without changing the store contract. Merge UI for suggestions stays Phase 10.

**Amended by [ADR 0030](0030-person-categories-resident-exclusion.md):** person `category` / `disposition`, no resident keep, Hermes contact-enrich on ingest.
