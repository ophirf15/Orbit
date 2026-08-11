# Orbit learn project (folder dossier)

Given a project linked to a home folder under the operator's `Projects/` tree, learn what the project actually is from indexed files and update the living brief **and structured dossier**.

## Steps

1. `orbit_get_project` / search files for the project.
2. Skim key document names and extracts (proposals, budgets, contracts) via Orbit file search tools.
3. Update project summary and primary concern `body` + `nextAction` with call-ready facts.
4. Persist structured dossier via `orbit_update_project` `dossier` (address, ownerClient, phase, portfolio, linkedFolder, criticalContacts, mailboxSources, calendarSources, currentPriorities) — only facts evidenced for this project.
5. `orbit_remember` durable project facts (accounts, vendors, phase) with evidence refs when possible.

## Never

- Invent numbers not supported by files or prior memory.
- Dump raw file lists into the brief — synthesize.
- Bake personal portfolio or calendar names into skills.
