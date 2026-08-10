# Orbit ↔ Hermes under the hood

Reference clone: `vendor/hermes-agent` (gitignored). Upstream: https://github.com/nousresearch/hermes-agent

## How we connect (no Hermes fork)

| Layer | What Orbit does | Hermes surface |
|---|---|---|
| Identity | Write `SOUL.md` | Prompt slot #1 |
| Procedures | Install `skills/orbit/*/SKILL.md` | Skills hub / progressive disclosure |
| Tools | Register `mcp_servers.orbit` → Orbit.Mcp | MCP stdio → Core HTTP |
| Wake | Host `OperatorWakeService` + ambient timer | API `:8642` runs/chat, session `orbit-operator` |
| Channels | Provenance + Pulse | Telegram gateway owned by Hermes |
| Plugin | Deferred | `$HERMES_HOME/plugins/` only if MCP is insufficient |

ADR: `docs/decisions/0027-orbit-hermes-integration.md`

## Provision

Orbit Settings → **This PC** mode → **Connect Hermes** (one click):

1. Finds `%LOCALAPPDATA%\hermes` (or `$HERMES_HOME`)
2. Exchanges keys: writes `API_SERVER_*` into Hermes `.env`, stores Hermes key in Orbit; writes `ORBIT_CORE_URL` / `ORBIT_API_KEY` into Hermes `.env`
3. Provisions SOUL, `skills/orbit/*/SKILL.md`, merges `mcp_servers.orbit`, plugin marker under `plugins/orbit/`
4. Restarts gateway and waits until `:8642` health succeeds
5. Status indicator turns green when connected

**Manual / remote** mode shows URL + API key fields for non-local Hermes.

## Stack choice (tight integration, no fork)

| Use | Why |
|---|---|
| SOUL + skills + MCP + gateway API | Official extension surfaces; survives Hermes upgrades |
| Host ambient timers | Reliable Windows day-loop; Hermes cron optional later |
| Plugin | Defer — in-process Python, version-coupled; only if MCP can't hook |

## Upstream gotchas

- Tool names: `mcp_orbit_*` not `mcp__orbit__*`
- No gateway ⇒ no `:8642`
- Session continuity: `X-Hermes-Session-Key: orbit-operator`
- Data home is `%LOCALAPPDATA%\hermes\` — wipe `hermes-agent\` to reinstall code, not the whole tree

## Windows install (operator)

```powershell
iex (irm https://hermes-agent.nousresearch.com/install.ps1)
hermes gateway install
hermes gateway start
```

Orbit does not maintain Hermes — we configure it.
