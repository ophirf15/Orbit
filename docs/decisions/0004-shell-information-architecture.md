# ADR 0004 — Shell information architecture

## Status

Accepted (Phase 2)

## Context

Orbit needs a native WinUI shell before business features. The constitution forbids SaaS dashboard chrome and requires Limbo to stay visible.

## Decision

- Left-compact `NavigationView`: Workbench, People, Files; footer Settings + About
- Workbench is home: adaptive empty project cells + always-visible Limbo strip + Hermes status text (not connected)
- Ctrl+K command palette lists nav commands only (plus theme toggle)
- People/Files are stub pages until Phases 8/6
- No KPI cards, charts, or fake business data

## Consequences

Phase 5 fills workbench cells and Limbo capture (`docs/decisions/0007-workbench-capture.md`). Phase 16 powers real search. Shell destinations stay stable for the palette; `capture.quick` is an action command, not a nav destination.
