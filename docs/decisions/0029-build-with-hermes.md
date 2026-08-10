# ADR 0029 — Build with Hermes (MCP + skills + cron + webhooks)

## Status

Accepted — 2026-08-09

## Context

ADR 0028 flipped cadence ownership to Hermes. Plan 023 / Hermes consult (`orbit-workjarvis-plan-v2`) confirmed the product anti-pattern: treating Hermes as something to reverse-engineer on the Dev PC instead of hiring it with a portable employee pack.

Hermes 0.20 guidance (and an empty `plugins/orbit` README already in HERMES_HOME): Orbit domain state must not live in a Hermes Python plugin.

## Decision

1. **Build path** = portable pack (SOUL, `skills/orbit/*/SKILL.md`, cron + webhook manifests, scripts) + Orbit MCP/Core APIs + Connect materialization.
2. **No Orbit domain plugin.** Plugins remain deferred unless Hermes gains a capability MCP cannot express.
3. **Cron → Orbit UI** uses `orbit_report_briefing` so Pulse / Hermes strip update without Host identity wakes.
4. **Connect** owns skill collision quarantine (flat `skills/<name>.md` vs nested SKILL.md) and webhook adapter enablement (`WEBHOOK_*` + `platforms.webhook` routes).
5. **Control plane** for Host-initiated operator work prefers `/v1/runs`; chat completions remain for interactive streaming only.

## Consequences

- Prod installs are proven by provisioning an empty HERMES_HOME from manifests, never cloning Dev sessions/secrets/`jobs.json`.
- Hermes downtime still falls back to Core ingest + slim Host wake + heuristics (ADR 0012 floor).
