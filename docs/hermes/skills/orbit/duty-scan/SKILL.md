---
name: duty-scan
description: "Morning/evening duty scan over calendar, blockers, and stalled work via Orbit tools."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Duty scan (Orbit operator)

Morning and evening duty scan for Hermes when Orbit Core is reachable.

## Purpose

1. Pull calendar window + open blockers + stalled waiting tasks via Orbit tools.
2. Rank next steps with evidence (`orbit_search` / `orbit_get_related_context`).
3. Apply standing rules only for covered big moves; otherwise propose suggestions.
4. Remember durable facts with `orbit_remember` when the operator learns preferences.
5. **Always finish by calling `orbit_report_briefing`** with the ranked briefing (or `[SILENT]` if nothing material). That is how Orbit Pulse / the Hermes strip show your work — cron delivery alone does not update Orbit.

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

Orbit emits real change signals (`email.ingested`, material calendar/task changes). Periodic duty cadence is **Hermes cron** (ADR 0028), not Host five-minute identity pokes.

When Pending contacts appear after mail, run **contact-enrich** (classify company/client/vendor or flag residents; canonicalize duplicate org names; write phones) — see `skills/orbit/contact-enrich`.
