---
name: orbit-orient
description: "Interactive orientation: load Pulse/workbench/memory once, then advise — Work Jarvis warm start."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Orbit orient (interactive)

Use this in **interactive** sessions (Agent chat, Telegram, Hermes dashboard) when the operator asks what you know, what’s going on, or wants advice about work.

## Purpose

Start warm like Jarvis: load the current board once, then advise. Do not fish the whole inbox with many searches unless the operator asks for a deep audit.

## Steps

1. `orbit_list_memory` (global) — operator dossier + standing prefs.
2. `orbit_get_workbench` and/or Pulse tools — open concerns, day brief if available.
3. Optionally `orbit_get_calendar_context` when timing matters.
4. Answer from that snapshot. Go deeper with `orbit_search` / task emails **only** for the threads the operator names or the top blockers you just ranked.
5. If the dossier is thin (no role/style/priorities), ask **one** focused followup and `orbit_remember` the answer (`preference` / `working_style` / `person_fact`).

## Never

- Open with a dozen `orbit_search` / `orbit_list_task_emails` calls “to get a sense.”
- Invent who the operator is when memory is empty.
- Treat chat history alone as the source of truth for projects or mail.
