# ADR 0023 — Duty operator (hybrid wake, standing rules, memory)

## Status

Accepted — **LLM ambient wake cadence superseded by [ADR 0028](0028-hermes-owns-routines.md)** (Hermes cron owns routines; Host keeps event signals + Hermes-down floor).

## Context

Phases 09–12 shipped Hermes chat + heuristic ambient suggestions (ADR 0012). Hermes only reasoned on user chat/capture; `AgentEventWorker` never woke Hermes. That left Orbit feeling like a small-task bot instead of a duty monitor that learns preferences and acts under standing rules.

Hermes already supports mail/calendar skills (Google Workspace, Outlook MCP/community skills). Orbit should stay the authoritative graph; Hermes should feed and advise.

## Decision

1. **Hybrid wake.** Orbit Host `OperatorWakeService` debounces graph events (+ calendar-soon) and starts an operator Hermes run when healthy. Hermes cron (documented, not implemented in Orbit) runs morning/evening duty scans via Orbit tools. Heuristic suggestions remain the Hermes-down fallback (ADR 0012).

2. **Standing rules.** `operator_rules` gates big moves: matching `create_task` / `update_task` / `set_blocker` / `link_email_thread` / `create_note` may auto-apply with audit `via=standing_rule`. Unmatched structural proposals stay accept/reject suggestions. “Always do this” on accept inserts an enabled rule.

3. **Operator memory.** `operator_memory` stores curated facts (preference, working_style, project_fact, person_fact, process). Hermes tools `orbit_remember` / `orbit_forget`; Core injects a capped memory block into operator prompts beside runtime context. Not a chat transcript dump.

4. **Mail/calendar feed.** Prefer Hermes skills as the live connector; Hermes writes into Orbit via ingest/mutation tools. Orbit Classic Outlook DnD + ICS/COM remain local fallback. Orbit Graph inbox sync is not the primary path for this slice.

5. **Operator runs.** Persist `operator_runs` (trigger, session/run id, status, briefing summary). Prefer Hermes `/v1/runs` when available; else durable session key `orbit-operator` + tool-enabled chat completions.

6. **Dashboard embed.** Amend ADR 0022: Orbit may host the Hermes dashboard (`:9119`) in WebView2 with browser fallback.

## Consequences

- Duty briefings appear without opening Agent chat when Hermes is reachable.
- Auto-moves require an explicit standing rule; default remains suggest-then-accept.
- Memory and rules survive restart and shape the next operator prompt.
- Portable Hermes pack documents duty-scan cron + mail/calendar skill setup.
