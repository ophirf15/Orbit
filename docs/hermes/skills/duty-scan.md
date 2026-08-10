# Duty scan (Orbit operator)

Morning and evening duty scan for Hermes when Orbit Core is reachable.

## Purpose

1. Pull calendar window + open blockers + stalled waiting tasks via Orbit tools.
2. Rank next steps with evidence (`orbit_search` / `orbit_get_related_context`).
3. Apply standing rules only for covered big moves; otherwise propose suggestions.
4. Remember durable facts with `orbit_remember` when the operator learns preferences.

## Prerequisites

- Orbit MCP merged (`docs/hermes/portable/mcp_servers.snippet.yaml`)
- `ORBIT_CORE_URL` + `ORBIT_API_KEY` in Hermes `.env`
- Mail/calendar: enable Google Workspace skill and/or Outlook MCP/skill on the Hermes dashboard; write into Orbit via ingest/mutation tools (do not treat Hermes mail as authoritative Orbit state)

## Suggested cron (Hermes host)

Configure on the Hermes machine (exact scheduler varies by Hermes version):

```text
# Morning 7:30 local
30 7 * * *  duty-scan morning

# Evening 18:00 local
0 18 * * *  duty-scan evening
```

Prompt seed:

```text
Run an Orbit duty scan. Use orbit_get_workbench, orbit_get_calendar_context (or calendar tools),
orbit_list_rules, and orbit_list_memory. Produce a ranked next-steps briefing (max 8).
Call orbit_remember only for durable preferences/process facts.
```

Orbit also wakes Hermes on `email.ingested` / calendar-soon via `OperatorWakeService` (hybrid).
