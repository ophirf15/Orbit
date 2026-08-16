# ADR 0024 — Classic Outlook ingest client

## Status

Accepted (amended)

## Context

Hermes Docker mail skills and Graph (Entra client ID) are blocked or awkward for the operator. Classic Outlook is the daily mail client. Drag/drop `.msg` works (ADR 0009) but does not feel first-class. Users want a one-click push of selected mail into Orbit for the duty operator.

An in-proc .NET Framework COM add-in that **ingested** mail (`Orbit.OutlookAddIn`) produced `clr.dll` AVs on Outlook 16.0.20228. An Office.js web add-in + Vite host was prototyped but adds a second server, HTTPS certs, and Host API keys from Outlook — wrong shape for “Adobe-style handoff.”

Out-of-process Outlook COM (already used for calendar and App push) is stable.

## Decision

1. **Primary path — App handoff:** Classic Outlook ribbon **Send to Orbit** (`Orbit.OutlookLauncher`) only `ShellExecute`s `orbit://push-outlook` (or `Orbit.App.exe --push-outlook`). The already-open Orbit App focuses, shows memo + optional project, then OOP COM `SaveAs` `.msg` → existing `POST /v1/emails/ingest` (optional `memo` in wake). No Outlook→Host HTTP, no Vite, no add-in API keys.
2. **App shortcuts:** Ctrl+Shift+O / Workbench **Push Outlook mail** use the same App pull + memo dialog.
3. **Office.js web add-in** (`src/Orbit.OutlookWebAddIn`) and Host `POST /v1/emails/from-outlook` remain **experimental / parked** — not the daily driver.
4. **In-proc ingest COM** (`Orbit.OutlookAddIn`, `scripts/register-outlook-addin.ps1`) stays **deprecated**; do not register. Use Settings → **Classic Outlook add-in** → Install / Update (or `scripts/register-outlook-launcher.ps1`) for the thin launcher only.
5. **Installer:** `pack-installer.ps1` publishes `outlook-launcher\` next to the app; Inno stages it under `{app}` and `%LocalAppData%\Orbit\OutlookLauncher`. Registration is **per-user HKCU** (not HKLM / elevated admin hive): Setup runs `Orbit.App.exe --install-outlook-addin` with `runasoriginaluser`, and App launch calls `EnsureRegisteredOnLaunch` when the add-in is missing or Outlook-disabled. Settings → Install / Update remains the repair path.
6. **Hermes** does not read Outlook; it reasons after ingest (memo in `email.ingested` payload + capture note).
7. **Drag/drop remains fallback** (ADR 0009). Graph remains future when IT issues a public client ID.

## Consequences

- Ribbon button without a second web stack; full `.msg` body via App COM.
- Thin launcher still loads CLR into Outlook (launch-only). If a build quarantines it, fall back to Ctrl+Shift+O / `orbit://push-outlook` from a shortcut.
- Experimental COM ingest registration must stay off on machines that hit the clr.dll AV.
