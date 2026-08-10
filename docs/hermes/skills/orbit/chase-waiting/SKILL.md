---
name: chase-waiting
description: "Chase stalled waiting/blocked Orbit tasks with concrete next actions."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Chase waiting

Find orbit tasks in waiting/blocked state that have gone stale. Update `nextAction` and living brief with a concrete chase step via Orbit tools.

## Steps

1. List open waiting/blocked tasks.
2. For each stale item: set a chase nextAction (who to ping, what to ask).
3. Update body with one-line status.
4. Brief the operator with ranked chase list via `orbit_report_briefing` (`triggerKind=chase.waiting`), or `[SILENT]` if nothing is stale.
