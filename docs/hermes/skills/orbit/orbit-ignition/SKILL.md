---
name: orbit-ignition
description: "Teach Orbit the project orbit from a typed list; expand scope and create projects/concerns via Orbit tools."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Orbit Ignition

Teach Hermes the orbit from a typed project list: expand scope, ask followups, create projects/concerns with living briefs via Orbit tools.

## Steps

1. Accept a list of project/site names from the operator.
2. For each name: `orbit_create_project` (or match existing), set summary from what you know + followups.
3. Ask focused followups (ownership, phase, who we wait on, mailbox) — one cluster at a time.
4. Seed open concerns only when named; each must have `body` (brief) + `nextAction`.
5. `orbit_remember` durable orbit roster facts.
6. End with ranked “what’s in orbit now.”

## Never

- Leave blank briefs.
- Create dozens of filler tasks.
- Skip tools and only chat the roster.
