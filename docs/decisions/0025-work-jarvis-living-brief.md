# ADR 0025 — Work Jarvis / living brief

## Status

Accepted — 2026-08-09

## Context

Phases 05–20 delivered capture, graph, email ingest, and duty wake, but task open often showed blank parent Overview forms, Limbo clicks were menu-only, and Hermes briefed in chat without writing `next_action`/`body`. That inverted the product: the user fed the tool.

## Decision

1. Orbit is a personal **Work Jarvis**. Hermes has purpose (operator context + memory); the workbench is a **non-chat visual** surface for Hermes to see and update work.  
2. **Living brief** is the primary task/Limbo surface: what it is, next move, waiting/blockers, evidence + Open original. Forms and Agent chat are secondary.  
3. Task focus loads **by id**; never fall back to project Overview when a task was requested.  
4. Email duty **attaches to existing work** when possible; Host **ensures** non-empty `body` (brief) + `next_action` even if Hermes only chats or is down.  
5. Limbo opens a living brief (note text), not only Assign/Archive menu.  
6. Site-specific stories in operator context are illustrations, not product scope limits.

## Consequences

- UI work centers on `WorkbenchDetailPanel` brief-first + Limbo open path.  
- Host gains task-by-id read + post-ingest duty ensure.  
- `orbit_create_task` accepts `nextAction`/`body`.  
- Phase 22+ adds ambient heartbeat; 21 seeds identity in operator memory/prompts.
