# Orbit Outlook launcher (thin handoff)

Adobe-style button: **Send to Orbit** in Classic Outlook only launches the Orbit App (`orbit://push-outlook`). Orbit App pulls the selected mail via OOP COM and asks for a memo. **No Vite, no Host API keys from Outlook.**

## Setup (recommended)

1. Install / update Orbit (installer ships `outlook-launcher\` next to the app).
2. Open Orbit → **Settings → Mail & calendar → Classic Outlook add-in → Install / Update**.
3. Start Classic Outlook → Mail tab → **Send to Orbit**.

Close Outlook first if Install reports a DLL lock.

## Dev / script setup

```powershell
cd <Orbit-repo>
.\scripts\register-outlook-launcher.ps1
# or from a packed payload:
.\scripts\register-outlook-launcher.ps1 -PayloadDir artifacts\installer\publish\outlook-launcher
```

Unregister:

```powershell
.\scripts\register-outlook-launcher.ps1 -Unregister
# or Settings → Uninstall
```

## Do not use

- `scripts/register-outlook-addin.ps1` — old ingest COM (crashes some Outlook builds)
- `src/Orbit.OutlookWebAddIn` — experimental Office.js + Vite (parked)

## Fallback

If the ribbon button is missing or disabled: select mail in Outlook, focus Orbit, press **Ctrl+Shift+O** (or run `orbit://push-outlook` from Win+R).
