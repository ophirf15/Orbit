# ADR 0026 — Orbit is Hermes’s home GUI (native Hermes default)

## Status

Accepted — 2026-08-09

## Context

Phases 01–20 built a local platform (workbench cells, flyouts, Agent chat). Phase 21 patched living briefs into flyouts. That inverted the product: the operator fed Orbit. The real product is merging **Hermes** (employee runtime) with **Orbit** (authoritative graph + visual home).

Hermes historically paired via Docker Desktop ([ADR 0022](0022-hermes-pairing.md)). Nous now ships **native Windows** Hermes (`%LOCALAPPDATA%\hermes`, `SOUL.md`, skills, gateway). Docker is optional, not the Monday work-PC path.

## Decision

1. **Orbit App** is Hermes’s home GUI: Ignition → Pulse → full-page living briefs. Not a chatbot shell; Agent nav is diagnostics.
2. **Hermes** is the employee: `SOUL.md` identity, installed Orbit skills, MCP → Core, gateway (API + Telegram).
3. **Default deploy:** native Windows Hermes + `hermes gateway`. Docker compose remains advanced/optional.
4. **Day-1:** typed project list (Hermes expands + followups) + `Projects/` folder tree (index + learn). Fresh graph on work PC; do not depend on this machine’s Orbit DB.
5. **Email as-it-comes** updates Core + briefs; full mailbox later. Telegram topics must land in Orbit via tools.
6. Flyout workbench detail is retired as the primary open path ([ADR 0025](0025-work-jarvis-living-brief.md) living-brief intent kept; flyout surface superseded).

## Consequences

- New provisioner writes `SOUL.md`, `AGENTS.md`, and `skills/orbit-*` into `HERMES_HOME`.
- Host gains pulse + orbit roster APIs; ambient/day-loop timers live in Host.
- Settings “Prepare Docker” demoted; “Set up Hermes (native)” is the happy path after spike.
- Packaging checklist: Orbit installer + Hermes native provision for work PC.
