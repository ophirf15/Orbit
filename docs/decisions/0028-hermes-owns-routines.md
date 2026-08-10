# ADR 0028 — Hermes owns routines; Orbit owns truth and change signals

## Status

Accepted — 2026-08-09

## Context

ADR 0023/0027 chose a **hybrid wake**: Core Host `OperatorWakeService` debounces graph events and also runs ambient timers (`calendar.soon` ~5 min, `AmbientPulseService` ~45 min + morning/evening). Each Host wake rebuilds a full system prompt via `OperatorPromptBuilder` (persona + playbooks + memory dump) and starts a Hermes `/v1/runs` or chat turn.

On a live Hermes 0.20 install this produced many **1-message sessions** re-stating “You are Hermes…”, even when calendar/email/workbench had not materially changed. SOUL.md and Orbit skills were already provisioned into `HERMES_HOME`; Host did not trust them.

Upstream Hermes already provides:

- SOUL.md as system prompt slot #1
- Skills (`SKILL.md`) including on-the-fly creation/patching in interactive sessions
- Native cron (`cronjob` tool / `hermes cron` / `cron/jobs.json`) with `[SILENT]` delivery suppression
- **monitor_script** pre-dispatch: unchanged script output suppresses the LLM run entirely (cheaper than `[SILENT]` after a model call)
- Webhooks with optional filter scripts
- BOOT.md for gateway-start recovery (not a scheduler)

Hard limits (Hermes 0.20, confirmed with the agent + docs):

- Cron-run sessions **cannot** recursively create more cron jobs
- Cron runs are fresh sessions; do not rely on chat memory being present
- Agent cron runs have a short hard interrupt window — keep monitor scripts cheap
- Job model/provider pins are user/CLI owned; the agent cron tool should not set them
- Portable installs must not copy Dev-PC session DBs, secrets, or runtime cron state

## Decision

1. **Hermes owns cadence and routines.** Morning/evening duty scans, Pulse monitors, and other recurring checks are Hermes cron jobs attached to Orbit skills (+ optional monitor scripts). Identity and standing operating rules live in **SOUL.md** (and Orbit/Hermes memory), not in every Host wake prompt.

2. **Orbit Core owns authoritative work state and change signals.** Core continues to ingest email/calendar/tasks. It emits **narrow, idempotent events** (email ingested, material calendar/task/blocker changes) — not identity lectures. Preferred delivery: signed Hermes webhook. Interim: slim Host wake with trigger payload only (no persona/playbook/memory dump).

3. **Stop wasteful Host agent pokes.** Disable (or gate to “payload changed”) `calendar.soon` periodic agent runs and Host-owned ambient `duty.scan` LLM wakes. Host may still sync calendar data and compute “soon” as a **fact** for Pulse/UI and for change-feed events.

4. **Portable Orbit employee pack is the source of truth for new installs.** Versioned under `docs/hermes/portable/` (+ provisioner): SOUL orbit section, skills, monitor/filter scripts, **jobs.manifest.json** (environment-neutral), MCP snippet, webhook manifest, `.env.example`. Connect/Ignition **materializes** target-local cron/webhooks via `hermes cron create/edit` (or equivalent API). Never ship Dev-PC `jobs.json`, `sessions/`, `auth.json`, or API keys.

5. **OperatorPromptBuilder shrinks.** Event wakes carry trigger kind + compact payload (+ optional email snapshot). Playbooks move into skills. Memory belongs in Hermes/SOUL/`orbit_remember`, not a 3.5k dump every five minutes.

6. **BOOT.md (optional)** — gateway-start health + one bounded delta catch-up only. Not a substitute for cron.

7. **ADR 0027 item 5 and ADR 0023 “Host ambient timers for duty” are superseded** for LLM wakes. Host reliability floor (Hermes-down heuristics, email ingest floor) remains.

## Consequences

- Connect becomes “hire Hermes”: provision pack + MCP + cron manifests, then Hermes self-manages interval within safety rules (interactive sessions may adjust cron; cron sessions may not spawn cron).
- Need Core APIs: change cursor / pulse delta / typed calendar context / bulk blockers (see plan) so monitor scripts can no-op without LLM.
- Pulse UI still polls Core for display; that is not a Hermes poke.
- Tests assert: no periodic Host calendar.soon agent enqueue when meetings are static; provisioner writes manifest-driven jobs on a clean HERMES_HOME.
