# ADR 0007 — Workbench capture and expand-in-place

## Status

Accepted (Phase 5)

## Context

Orbit’s constitution requires instant capture without forced metadata, always-visible Limbo, and expand-in-place detail without deep navigation stacks. Core Host owns durable mutations.

## Decision

1. Every capture inserts a `notes` row with immutable `original_text`. Limbo sets `is_limbo=1`. Project capture also creates a `tasks` row so the cell shows a line.
2. `GET /v1/workbench` is the App’s aggregate read (cells + limbo). `POST /v1/notes` is the capture write. `GET /v1/projects/{id}/context` feeds the drawer.
3. Context opens as a right-hand drawer on the Workbench page (Escape / Close returns to the full grid). No Frame push for detail.
4. In-app quick capture uses Ctrl+N and the `capture.quick` palette command to focus Limbo. `IGlobalCaptureHotkeyRegistrar` is a no-op seam for a future OS-wide hotkey (packaging / later phase).

## Consequences

Agent suggestions may attach to notes but must never rewrite `original_text`. Assign/merge UX and real Hermes suggestion generation remain later phases.
