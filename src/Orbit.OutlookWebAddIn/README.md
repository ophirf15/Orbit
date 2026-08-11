# Orbit Outlook web add-in (experimental / parked)

> **Not the daily path.** Prefer the thin App handoff: Outlook **Send to Orbit** → `orbit://push-outlook` → Orbit App (see [`Orbit.OutlookLauncher`](../Orbit.OutlookLauncher/README.md) and ADR 0024).
>
> This Office.js + Vite project talks to Core Host over HTTP and needs HTTPS certs / API keys. Kept for experiments only.

Crash-safe **Office.js** task pane prototype. Replaces the experimental in-proc COM *ingest* add-in that AVs in `clr.dll`.

## What it does

1. Ribbon **Orbit** button on a read message → task pane
2. Shows subject / from / Message-ID
3. Memo for Hermes + optional project dropdown
4. `POST /v1/emails/from-outlook` on Core Host → OOP COM `.msg` SaveAs → existing ingest → Hermes wake

## Prerequisites

- Classic Outlook running and signed in
- Orbit Core Host listening (default `http://127.0.0.1:8741`)
- Node.js 18+

## One-time setup

```powershell
cd src\Orbit.OutlookWebAddIn
npm install
npm run certs
```

Trust the generated localhost HTTPS certificate if Windows prompts you (UAC). If `npm run certs` hangs on “Installing CA certificate…”, approve the elevation dialog, or install `~\.office-addin-dev-certs\ca.crt` into **Trusted Root Certification Authorities** manually. `npm run dev` / `npm run build` only *read* existing cert files and do not reinstall the CA.

## Run (dev)

Terminal 1 — serve the task pane over HTTPS:

```powershell
cd src\Orbit.OutlookWebAddIn
npm run dev
```

Leave that running. Then sideload the manifest:

1. Open https://aka.ms/olksideload (or Classic Outlook **File → Info → Manage Add-ins**)
2. **My add-ins → Custom add-ins → Add a custom add-in → Add from File…**
3. Choose `src\Orbit.OutlookWebAddIn\manifest.xml`
4. Open a mail → ribbon **Orbit** → confirm the pane says **Orbit hello — add-in loaded**

Optional auto-sideload helper (when the Office tooling supports your client):

```powershell
npm start
```

## Connection

- Default Host URL: `http://127.0.0.1:8741`
- On loopback with no Core API key, auth is not required
- If a key is configured, either:
  - open the pane’s **Connection** section and paste it, or
  - rely on `GET /v1/outlook-addin/bootstrap` (loopback-only) to deliver the key

## Cache / troubleshooting

Classic Outlook caches manifests. After changing `manifest.xml`:

1. Remove the custom add-in from **My add-ins**
2. Delete `%LOCALAPPDATA%\Microsoft\Office\16.0\Wef` (Outlook closed)
3. Re-sideload the manifest

If the pane says it cannot open localhost, re-run `npm run certs` and restart Outlook.

**Do not** run `scripts/register-outlook-addin.ps1` — that registers the old in-proc COM add-in.

## Port / bind notes

- Orbit Core Host defaults to port **8741**. **Orbit-as-agent**’s Node runtime also defaults to `8741` — if both run, the add-in hits the wrong server and gets **404**.
- If Core Host Settings bind to a LAN IP, Host also listens on `127.0.0.1` so this add-in can keep using `http://127.0.0.1:8741`. Non-loopback bind requires an API key; the pane loads it via `GET /v1/outlook-addin/bootstrap` on the same machine.
- Connection panel: paste Host URL / API key only if auto-bootstrap fails (`%LocalAppData%\Orbit\core-host-api-key.txt`).

## Uninstall

**My add-ins → Custom add-ins** → remove **Orbit**, then stop `npm run dev`.
