---
name: orbit-ignition
description: "Teach Orbit the project orbit from a typed list; expand scope and create projects/concerns via Orbit tools."
version: 0.1.1
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
2. For each name: **search first** (`orbit_get_project` / workbench / search). If a match or alias hit exists, **attach** — do not create a twin.
3. Only when unmatched: `orbit_create_project`. If the tool returns near-duplicate **candidates** (409), pick an existing project or ask the operator before `force=true`.
4. Set summary from what you know + followups; add operator nicknames with `orbit_add_project_alias` when they say “also known as…”.
5. Ask focused followups (ownership, phase, who we wait on, mailbox) — one cluster at a time.
6. Seed open concerns only when named; each must have `body` (brief) + `nextAction`.
7. `orbit_remember` durable orbit roster facts (`project_fact`, scope=project id when known).
8. **Operator dossier** (once if thin): ask a short cluster — role/company, how they want briefs (tone/length), standing priorities, anything to deprioritize. `orbit_remember` as `working_style` / `preference` / `person_fact` (global). Also distill into Hermes lasting memory.
9. End with ranked “what’s in orbit now.”

## Never

- Leave blank briefs.
- Create dozens of filler tasks.
- Skip tools and only chat the roster.
- Bake personal/site nicknames into skills or assume a fixed portfolio.
- Skip the dossier when `orbit_list_memory` is empty of role/style facts.
