---
name: pulse-refresh
description: "Rebuild Pulse awareness: ensure briefs and next actions on active orbit concerns."
version: 0.1.1
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Pulse refresh

Rebuild awareness of the active orbit: rank concerns, ensure briefs, surface waiting/chase.

## Steps

1. List orbit projects and open tasks via Orbit tools.
2. Ensure each active concern has non-empty `body` and `nextAction` (update if blank).
3. Produce a short ranked next-steps briefing (max 8) of what you already fixed/updated.
4. Call `orbit_report_briefing` with that briefing and `triggerKind=pulse.refresh` (or `[SILENT]` if unchanged / nothing to say).
5. If material (not `[SILENT]`), run **briefing-distill** so standing truths land in Hermes lasting memory and `orbit_remember`.

## Never

- Chat-only briefing with empty Core fields.
- Review-unassigned busywork.
- Skipping `orbit_report_briefing` — Pulse will not update without it.
- Remembering the entire Pulse dump as memory.
