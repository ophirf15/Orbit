# Orbit ↔ Hermes portable pack

Copy **this entire folder** to a new Hermes install (or keep it checked out from the Orbit repo). Fill secrets locally — never commit real keys.

## Pack layout (ADR 0028)

Hermes owns cadence; Orbit ships a versioned, secret-free "employee kit" that `HermesHomeProvisioner` materializes into `$HERMES_HOME` on Connect. Nothing here contains chat IDs, tokens, or absolute Dev-PC paths — those are filled locally (env vars, Connect & save) after the pack lands.

| File | Materializes to | Role |
|---|---|---|
| `../SOUL.md` (marked `<!-- orbit:soul -->` section) | `$HERMES_HOME/SOUL.md` | Identity once; provisioner merges only the marked region, preserving anything an operator added outside it |
| `../skills/orbit/*/SKILL.md` | `$HERMES_HOME/skills/orbit/*/SKILL.md` | Procedures Hermes cron + interactive skills point at (duty-scan, pulse-refresh, orbit-orient, briefing-distill, …) |
| `scripts/orbit-pulse-monitor.py` | `$HERMES_HOME/scripts/orbit-pulse-monitor.py` | Cron `script=` pre-check for the Pulse monitor job — unchanged snapshot hash emits `{"wakeAgent": false}`, no LLM call |
| `scripts/orbit-event-filter.py` | `$HERMES_HOME/scripts/orbit-event-filter.py` | Webhook route `script=` — dedupes event ids, drops junk with `[SILENT]` / `{"__hermes_ignore__": true}` |
| `cron/jobs.manifest.json` | `$HERMES_HOME/orbit/jobs.manifest.json` (copy) + materialized into `$HERMES_HOME/cron/jobs.json` | Source of truth for morning/evening duty-scan + Pulse monitor jobs; the provisioner upserts by logical `name`, so re-running Connect never duplicates jobs |
| `webhooks.manifest.json` | `$HERMES_HOME/orbit/webhooks.manifest.json` (copy) + `platforms.webhook` in `config.yaml` | Route stub for `orbit-email-ingested`. Connect writes HMAC to Hermes `WEBHOOK_*` / `ORBIT_HERMES_WEBHOOK_SECRET` and `%LocalAppData%\Orbit\hermes-webhook-secret.txt` so Core can POST. |
| `BOOT.md.template` | `$HERMES_HOME/BOOT.md` (written if absent) | Gateway-start health check + one bounded catch-up only — not a scheduler |
| `mcp_servers.snippet.yaml` + `.env.example` | `config.yaml` / `.env` (merge) | Existing MCP wiring; keep secret-free |

`cron/jobs.json` on a fresh install is **generated**, never copied from the Dev PC — see `docs/decisions/0028-hermes-owns-routines.md` for why (no Dev-PC sessions/auth/secrets on a new Hermes host).

Prefer **Orbit Settings** pairing (ADR 0022) when you can:

| Goal | In Orbit Settings |
|---|---|
| Local Hermes on this PC | **Prepare local Hermes folder** → `docker compose up -d` → **Open in Orbit** / browser dashboard (provider + Telegram + mail skills) → **Connect & save** |
| Hermes already running (LAN / Tailscale) | Paste API URL + `API_SERVER_KEY` → **Connect & save**; dashboard for admin |
| Hermes tools calling Orbit | **Copy Core env for Hermes** → paste into `~/.hermes/.env` → reload MCP |
| Duty scans / routines | Hermes cron materialized from `cron/jobs.manifest.json` (ADR 0028) by `HermesHomeProvisioner`. Host no longer owns ambient LLM pokes. `duty_scan_cron.snippet.md` remains a checklist for hand-editing an existing install. |
| Skill collisions | Connect quarantines flat `skills/<name>.md` when `skills/orbit/<name>/SKILL.md` exists (Hermes refuses ambiguous names). |

**Dev cleanup:** if cron logs say `Ambiguous skill name 'duty-scan'`, delete or move flat `skills/duty-scan.md` (and pulse-refresh / orbit-ignition / chase-waiting) — Connect does this automatically.

Hermes has **no OAuth/device-code** for OpenAI-compatible API clients (Orbit, Open WebUI, etc.). Auth is always Bearer `API_SERVER_KEY`. Dashboard login (basic auth or Nous OAuth) is a **separate** surface on port **9119** — that is where users connect AI providers and Telegram.

## Remote home / work

Recommended: put Hermes and the work PC on the same **Tailscale** (or other VPN) mesh.

1. On Hermes: bind API to the Tailscale IP (or `0.0.0.0` on a private mesh only) with a strong `API_SERVER_KEY`.
2. In Orbit: Hermes URL `http://100.x.y.z:8642` (Tailscale) → paste key → **Connect & save**.
3. Set Core Host bind to this PC’s Tailscale/LAN IP (not `127.0.0.1`), Save, then **Copy Core env for Hermes**.
4. Avoid exposing `:8642` on the public internet as plain HTTP.

## Prod PC checklist (fresh Orbit + Hermes)

Default: **native Windows Hermes** (same as Dev: `%LocalAppData%\hermes` via install.ps1). Docker compose in this pack remains an alternate.

1. Install Orbit App + Core; confirm fresh SQLite and Core API key in Settings.
2. Install Hermes natively; complete model/provider auth in the Hermes dashboard (`:9119`).
3. Optional: Telegram / mail skills in the dashboard.
4. Orbit **Connect & save** (passes `docs/hermes` into the provisioner): MCP env, SOUL merge, skills, cron jobs, webhook routes + shared HMAC sidecar, BOOT.md if absent.
5. Health gate: Hermes MCP can `orbit_get_workbench`; create a throwaway test task; push a test email or hit webhook health on `:8644`.
6. Confirm cron jobs exist (`hermes cron list` / `cron/jobs.json`) **after** step 5.
7. Grant Outlook add-in / calendar sources as needed.

**Never copy from Dev:** `sessions/`, `memories/`, `.env` secrets, OAuth tokens, `cron/jobs.json`, cron execution DB, Orbit `orbit.db`, absolute paths, machine IDs, webhook secrets.

### Contacts cleanup (ADR 0030)

After upgrading, open People → **Pending** and **Review**. Confirm accidental residents with **Confirm not tracking** (sets `excluded_resident`). Hermes `contact-enrich` on new mail should classify company/client/vendor or flag residents — institutional/campus brands never by domain alone. Re-Connect Hermes so `skills/orbit/contact-enrich` and MCP contact tools land.

Secondary-home proof on Dev: provision a temp folder with `HermesHomeProvisioner.Provision(hermesHome: <empty>, docsHermesRoot: <repo>/docs/hermes)` and assert skills + 4 cron jobs + webhook route without Dev paths — covered by unit tests.

## Topology

```text
┌─────────────────────────────┐     LAN / Tailscale      ┌──────────────────────────────┐
│ Windows (Orbit App + Core)  │◄────────────────────────►│ Hermes host                  │
│ Core: http://<ip>:8741      │   ORBIT_CORE_URL + key   │ API :8642 + API_SERVER_KEY   │
│ App → Hermes :8642 + key    │◄────────────────────────►│ mcp_servers.orbit → Orbit.Mcp │
│ {app}\orbit-mcp\ (shipped)  │  Connect syncs to        │ %LocalAppData%\Orbit\orbit-mcp│
└─────────────────────────────┘  LocalAppData            └──────────────────────────────┘
```

Installer ships self-contained `Orbit.Mcp.exe` under `{app}\orbit-mcp\` and stages the same folder into `%LocalAppData%\Orbit\orbit-mcp\`. Connect rewrites Hermes `mcp_servers.orbit` to that LocalAppData path — no manual `dotnet publish` on the work PC.

App-managed `docker compose up` from Settings is still a follow-on; local folder prep + Connect & save cover the installer-friendly path (ADR 0022).

Tool names appear to the model as `mcp_orbit_<tool>` (Hermes prefixes the server key `orbit`).
