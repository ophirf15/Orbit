# ADR 0002 — Foundation is development-time only

## Status

Accepted (Phase 1)

## Context

Foundation holds harvested modules from prior projects. Orbit must remain a standalone installed application.

## Decision

Consult Foundation at development time for patterns. Copy/adapt into Orbit source only when intentional. **Never** take a runtime package or service dependency on Foundation.

## Consequences

Harvest decisions are recorded in `docs/foundation-harvest.md`. Codex OAuth stays with Hermes; desktop updater concepts are deferred to Phase 17.
