# ADR 0013 — Context bundle and per-claim disambiguation

## Status

Accepted (Phase 11)

## Context

Shared vendors (e.g. MetroFiber) and dual-linked emails must not merge project task contexts. Agents need a bounded evidence pack for a project/workstream/task instead of dumping the graph. Ambiguous claims must stay visible as suggestions — never silent assigns. Calendar meeting content remains Phase 12.

## Decision

1. **`ContextBundleService.GetBundle(targetType, targetId, attentionProjectId?)`** resolves project scope from `project` | `workstream` | `task`, then returns a bounded pack: tasks/blockers/notes, emails with **project-scoped** `email_extractions` only, contacts, files via `file_project_links`, pending suggestions, related entities (orgs/people with project-scoped relationship rows). **`meetings` is always `[]` until Phase 12.**
2. **Host**: `GET /v1/context/bundle?targetType=&targetId=` (+ optional `attentionProjectId`, weak alignment flag only). Agent tool **`orbit_get_related_context`** wraps the same service.
3. **`MultiProjectClaimSplitter`** runs after email ingest (heuristic, no LLM). When the body/subject mentions ≥1 known project name/code, ensure `email_project_links` and per-project `email_extractions` without overwriting other projects' rows. When actionable language has **no** project name/code, create `disambiguate_email_claim` suggestion — not a hard extraction.
4. Vendor org identity ≠ project identity: MetroFiber appears in both Harbor Court and Riverview bundles as related entities while tasks/extractions stay project-filtered.
5. Workbench project drawer surfaces **Related files** (open / preview) from context `files`.

## Consequences

Hermes grounds answers on the bundle. Dual-site emails keep separate claim rows. Ambiguity stays in `agent_suggestions`. Full calendar intelligence and ranked search remain later phases.
