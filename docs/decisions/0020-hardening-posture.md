# ADR 0020 — Hardening posture for v1

## Status

Accepted (Phase 18)

## Context

Orbit holds project folders, email artifacts, contacts, and agent-driven mutations. Phase 18 must prove the trust boundary is durable: PathGuard stays strict, agent tools stay allowlisted, mutations stay audited, and untrusted email/HTML content cannot escalate privileges. Live Hermes/Telegram/Outlook/signing remain environment work; automated proofs plus an acceptance map define v1 readiness.

## Decision

1. **PathGuard unchanged.** External/project files stay read-only. Explicit `/v1/files/external/{delete,rename,move,write}` routes remain 403 by construction. Writes only under the generated-files root.
2. **Bearer auth.** When a Core API key is configured (or bind is non-loopback), non-health requests require `Authorization: Bearer`. `/v1/health` stays anonymous for liveness.
3. **Allowlisted agent tools.** Only mapped `/v1/agent/tools/orbit_*` routes exist. Unknown tool names return 404. No SQL/shell capability routes.
4. **Audited mutations.** Typed mutation tools write `audit_events` (actor + detail JSON, optional provenance).
5. **Untrusted content as data.** Email/HTML bodies (including prompt-injection-like text) are stored and previewed as data. Ingest never grants capabilities or weakens PathGuard.
6. **Redacted diagnostics.** `GET /v1/diagnostics` and `POST /v1/diagnostics/export` emit schema version, sync summary, index counts, Hermes last-known health, calendar provider status, and capability list. Excluded by default: API keys, Hermes key file contents, email bodies.
7. **Acceptance map.** `docs/v1-acceptance.md` records scenario steps 1–19 as Automated / Manual / Blocked-TODO. Pipeline 07–18 complete for code; human review queue lives in `docs/TODO.md`.

## Consequences

- Hardening regression suite (`HardeningTests`) must stay green with `.\build.ps1 -Test`.
- Operators export diagnostics from About or Settings without shipping secrets.
- Completing remaining Manual/Blocked-TODO acceptance rows is a review-queue job, not a Host code blocker for marking Phase 18 Done.
