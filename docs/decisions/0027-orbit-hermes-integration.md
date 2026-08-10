# ADR 0027 — Orbit ↔ Hermes integration (no fork)

## Status

Accepted — 2026-08-09

## Context

Hermes Agent ([nousresearch/hermes-agent](https://github.com/nousresearch/hermes-agent), vendored for reference under `vendor/hermes-agent`) is the employee runtime. Orbit must not fork or rewrite it. Windows native install uses `%LOCALAPPDATA%\hermes` (`HERMES_HOME`).

Hermes extension surfaces (from upstream):

| Surface | Mechanism |
|---|---|
| Identity | `$HERMES_HOME/SOUL.md` (system prompt slot #1) |
| Skills | `$HERMES_HOME/skills/<category>/<skill>/SKILL.md` + optional `skills.external_dirs` |
| MCP | `config.yaml` → `mcp_servers:` + secrets in `.env` |
| Plugins | `$HERMES_HOME/plugins/<name>/` with `plugin.yaml` (optional later) |
| API | Gateway `:8642` — chat/completions, runs, sessions |
| Cron | `$HERMES_HOME/cron/jobs.json` via gateway |
| Messaging | Telegram etc. on Hermes gateway |

## Decision

**Primary stack (all of the above that we need, no Hermes fork):**

1. **SOUL.md** — Orbit Work Jarvis persona (operator-learned, not hard-branded).
2. **Skills** — Orbit procedures as Hermes-native `SKILL.md` trees under `skills/orbit/*`.
3. **MCP** — `Orbit.Mcp` registered as `mcp_servers.orbit` pointing at Core (`ORBIT_CORE_URL` / `ORBIT_API_KEY`).
4. **API** — Orbit Host/App wake Hermes via existing `IHermesClient` (`orbit-operator` session).
5. **Cron** — ~~prefer Orbit Host ambient timers; optional Hermes cron~~ **Superseded by ADR 0028:** Hermes owns routine cadence; Host emits change signals / slim event wakes only.
6. **Plugin** — deferred unless MCP cannot express a needed hook; then thin `$HERMES_HOME/plugins/orbit-core` calling Core HTTP only.

**Not in scope:** rewriting Hermes Desktop/TUI, patching Hermes source, maintaining a fork.

## Consequences

- `HermesHomeProvisioner` writes SOUL, `skills/orbit/*/SKILL.md`, merges `mcp_servers.orbit` into `config.yaml`, and documents `.env` Core vars.
- Prefer putting operator rules in **SOUL.md**. Upstream does **not** auto-load `$HERMES_HOME/AGENTS.md` (AGENTS.md is project cwd / git-root only). Provisioner may still write `AGENTS.md` for humans / when wake `workdir` points at a tree that includes it.
- MCP tools register as `mcp_orbit_<tool>` (single underscores), not `mcp__orbit__…`.
- Portable pack remains for Linux/remote Hermes hosts.
- Vendored `vendor/hermes-agent` is reference-only (gitignored); do not ship in installer.
