# ADR 0017 — Malleability with scoped self-modification

## Status

Accepted (Phase 15)

## Context

Orbit should evolve while in use (custom fields, saved views) without granting Hermes arbitrary shell, project-folder write, or in-place binary patching. Some requests still need source changes; those must be gated and repo-scoped.

## Decision

1. **Custom fields and layouts live in SQLite** as validated data (`custom_field_definitions` / `custom_field_values`, `layout_definitions` / `layout_revisions`), not compiled XAML. Field types: text, number, bool, date, choice.
2. **Capability scopes are separated** in the catalog:
   - normal operator tools
   - runtime configuration/schema tools (`orbit_add_custom_field`, layout save/apply/revert)
   - developer/source tools (`orbit_dev_*`)
3. **Developer tools** require `DeveloperMode` and configured `SourceRepoRoot`. Paths outside the repo and attached project folders are denied. Telegram channel is **403** unless `DeveloperRemoteOverride` (default false).
4. **Hermes skills** under `docs/hermes/skills/` are procedure docs that call typed Orbit tools; they grant no OS permissions.
5. **Delivery** of source changes goes through normal update paths — never by patching installed binaries.

## Consequences

CI covers field add/set, layout save/revert, developer path denial (project folders + telegram), and skill file presence. Full auto-PR/GitHub remain stubbed; `orbit_dev_create_branch` plus optional repo-only write/`dotnet build` are enough for the Phase 15 AC.
