# ADR 0022 — Hermes pairing (local + remote)

## Status

Accepted

## Context

Orbit talks to Hermes over the OpenAI-compatible API (`:8642`) with Bearer `API_SERVER_KEY`. Hermes has no OAuth/device-code flow for that API surface (Open WebUI and similar clients use the same shared secret). Dashboard OAuth (Nous Portal) and messaging DM pairing do **not** authenticate Orbit’s API client.

Operators also need Hermes→Orbit tools (`ORBIT_CORE_URL` + `ORBIT_API_KEY` via MCP). Manual `.env` edits on two machines caused stale-key failures when Hermes was restarted without reloading secrets.

Home/work split is a real topology: Hermes at home, Orbit at work. Hermes documents Tailscale / VPN for remote backends; public internet exposure of the API server should use HTTPS (or stay on a private mesh) plus a strong key.

## Decision

1. **Orbit owns pairing UX.** Settings exposes **Connect & save** (probe health + authenticated capabilities, then persist URL + key sidecar) and **Copy Core env for Hermes** (clipboard snippet for `ORBIT_CORE_URL` / `ORBIT_API_KEY`). No continuous `.env` sync.

2. **Auth probe is strict.** `/health` alone is not “connected.” Capabilities `401`/`403` (or other non-404 failures when a key is required) fail the connection test.

3. **Local path.** **Prepare local Hermes folder** writes `%LocalAppData%\Orbit\hermes-local\` (`docker-compose.yml` + `.env` with a generated `API_SERVER_KEY` **and** dashboard basic-auth), stores the API key in Orbit’s sidecar, and points the base URL at `http://127.0.0.1:8642`. Compose publishes **:8642 (API)** and **:9119 (dashboard)**. App-managed `docker compose up` remains a follow-on.

4. **Dashboard vs API.** Settings embeds the Hermes web dashboard (`:9119`) in a WebView2 pane for provider login, Telegram, and mail/calendar skill setup (ADR 0023). **Open in browser** remains the fallback when WebView2/OAuth redirects fail. Orbit Agent chat continues to use the API + `API_SERVER_KEY`.

5. **Remote path.** User enters a reachable Hermes API base URL (LAN, Tailscale IP, or HTTPS tunnel) and the Hermes `API_SERVER_KEY`. Prefer Tailscale/VPN over raw public HTTP. Dashboard, if exposed, is opened the same way (`:9119` on that host).

6. **Secrets.** Hermes API key, dashboard password, and Core API key stay in sidecars / Hermes env only — never in `settings.json` or git. Local dashboard credentials are also written to `hermes-local/dashboard-login.txt` for the operator.

## Consequences

- Installer users get a guided pair instead of tribal key copying.
- Fresh local installs use the in-app dashboard (or browser fallback) for Hermes-side setup; Orbit stays the thin API client + link.
- Remote home Hermes works when the API is reachable and the key matches; mesh/VPN is the recommended transport.
- Full Docker lifecycle from Settings stays deferred; local folder prep + Open dashboard cover the installer-friendly path.
