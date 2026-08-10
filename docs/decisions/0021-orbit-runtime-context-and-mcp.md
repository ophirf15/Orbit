# ADR 0021 — Runtime context and Orbit MCP portable pack

## Status

Accepted (TODO knock-out Phase E)

## Context

Agent chat previously talked to Hermes without injecting live Orbit UI state, and Hermes had no first-class registration path for Orbit Core tools beyond raw HTTP docs. Operators also need a takeaway-complete Hermes setup (test box at `192.168.1.19` and future prod) without tribal knowledge. App-managed Hermes Docker is desirable later but not required to unblock tool wiring.

## Decision

1. **Runtime context (Phase C).** The WinUI app builds an `OrbitRuntimeContext` (route/view, focus project/task, selection, data-root label, capability shortlist) and prepends it as an ephemeral system message on Hermes chat requests. It is not persisted as a conversation “You” row. Tools remain on Core; chat-completions `tools[]` is not used for Orbit domain tools.

2. **Tool bridge = Orbit-owned MCP stdio server.** New project `Orbit.Mcp` (`net9.0`) uses NuGet `ModelContextProtocol` with stdio transport. It exposes allowlisted wrappers — `orbit_get_related_context`, `orbit_search`, `orbit_get_project`, `orbit_get_contact`, `orbit_create_task`, `orbit_update_task` — that POST to Core Host `/v1/agent/tools/*` with `Authorization: Bearer` from env `ORBIT_CORE_URL` + `ORBIT_API_KEY`. Core remains the authority; MCP never opens SQLite or the filesystem.

3. **Hermes registers Orbit MCP as a client entry** under `mcp_servers` in `~/.hermes/config.yaml` (not Cursor/Runlayer MCP). Portable pack lives at `docs/hermes/portable/` (compose template, mcp/http snippets, `.env.example`, install checklist). Copy that folder to a fresh Hermes host.

4. **Topology.** Test: Hermes on LAN host (e.g. `192.168.1.19`); Orbit App + Core on Windows. Hermes↔Core needs a reachable Core URL (trusted-LAN bind + API key, or SSH tunnel). App↔Hermes uses Settings Hermes base URL (`http://192.168.1.19:8642` or tunneled loopback).

5. **Follow-on (not this pass):** App-managed Hermes Docker lifecycle (start/stop/health from Orbit Settings). Portable pack is the prerequisite; automation comes after it works manually.

## Consequences

- Solution gains `Orbit.Mcp` beside Host/App; Hermes operators publish or path a `dotnet`/`Orbit.Mcp` binary on the Hermes machine.
- Capability catalog / `docs/hermes/orbit-tools.md` remain the HTTP contract; MCP is a thin adapter.
- Secrets stay out of git: Core API key and Hermes `API_SERVER_KEY` only in env/sidecars.
