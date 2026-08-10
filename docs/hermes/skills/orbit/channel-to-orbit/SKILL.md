---
name: channel-to-orbit
description: "When work is discussed on Telegram or other channels, write concerns into Orbit so Pulse shows them."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Channel to Orbit

When the operator discusses work on Telegram or another Hermes channel, write it into Orbit so Pulse shows it.

## Steps

1. Identify whether this is a **new project**, a **sub-area** (workstream), or a **concern** (task) under an existing project.
2. New project: `orbit_create_project` with a clear name (+ summary). It lands in the Pulse orbit by default.
3. Sub-area under a project (e.g. FF&E, Internet): `orbit_create_workstream` with `projectId` + name. List with `orbit_list_workstreams` if unsure.
4. Concern: `orbit_create_task` / `orbit_update_task` with `body` + `nextAction`. Pass `workstreamId` when the task belongs to a sub-area.
5. Link email/files if referenced and available in Orbit.
6. Confirm briefly what landed in Orbit (and that Pulse will show it).

## Never

- Leave work-only in the chat transcript.
- Create Accept-chore suggestions instead of writing the concern.
- Pretend a nested "sub-project" table exists — use **workstreams** for sub-areas.
- Drive the Orbit GUI with computer-use to create projects/tasks.
