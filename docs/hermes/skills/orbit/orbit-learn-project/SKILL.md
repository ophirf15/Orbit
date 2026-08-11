---
name: orbit-learn-project
description: "Learn a project from its home folder dossier and update living briefs via Orbit tools."
version: 0.1.1
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis]
    category: orbit
---
# Orbit learn project (folder dossier)

Given a project linked to a home folder under the operator's `Projects/` tree, learn what the project actually is from indexed files and update the living brief **and structured dossier**.

## Steps

1. `orbit_get_project` / search files for the project.
2. Skim key document names and extracts (proposals, budgets, contracts) via Orbit file search tools.
3. Update project summary and primary concern `body` + `nextAction` with call-ready facts.
4. Persist structured dossier via `orbit_update_project` `dossier` fields (operator data only — never invent site names not evidenced):
   - `address` (property / site address)
   - `ownerClient`
   - `phase`
   - `portfolio`
   - `linkedFolder` (home path if known)
   - `criticalContacts` `[{name, role, personId, contact}]`
   - `mailboxSources` / `calendarSources` (labels or source ids the operator uses for this project)
   - `currentPriorities` (short list)
5. `orbit_remember` durable project facts (accounts, vendors, phase) with evidence refs when possible.

## Never

- Invent numbers not supported by files or prior memory.
- Dump raw file lists into the brief — synthesize.
- Bake personal portfolio or calendar names into skills — write only what this project's files evidence.
