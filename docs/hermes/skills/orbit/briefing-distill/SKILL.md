---
name: briefing-distill
description: "After Pulse/duty briefings, distill standing truths into Hermes lasting memory and orbit_remember."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Briefing distill

Close the Jarvis loop: Pulse and duty briefings are ephemeral. Compress only what should survive the next fresh session.

## When

- After a material `orbit_report_briefing` (not `[SILENT]`).
- After morning/evening duty scan or pulse-refresh that changed awareness.
- When the operator corrects you about how they work (“always…”, “never…”, “I am…”).

## Steps

1. Re-read the briefing you just produced (or the Pulse day brief / open concerns ranks).
2. Ask: what is **standing** vs **today only**?
   - Standing → Hermes lasting memory (your native distill) **and** `orbit_remember`.
   - Today only → leave on living briefs / next Pulse; do not remember.
3. `orbit_remember` with the right kind:
   - `working_style` — how they want briefs, tone, hours
   - `preference` — standing priorities, “don’t surface X”
   - `project_fact` — durable project/site facts (scope=`projectId` when known)
   - `person_fact` — key people / roles (not residents)
   - `process` — how Orbit+Hermes should handle recurring flows
4. Prefer updating/replacing stale facts over stacking near-duplicates (`orbit_list_memory` first).
5. Keep the dossier thin: a handful of high-signal lines, not a transcript.

## Never

- `orbit_remember` the full briefing text or every open task title.
- Remember one-off deadlines that belong on `nextAction` / `body`.
- Skip distill after a rich briefing “because cron is silent next time.”
