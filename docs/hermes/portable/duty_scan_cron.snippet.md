# Hermes cron snippet for Orbit duty scans

Merge into your Hermes scheduler / cron configuration on the Hermes host.
Exact keys depend on Hermes version — treat this as a checklist, not a drop-in file.

## Morning

- Time: `30 7 * * *` (local)
- Skill / prompt: `docs/hermes/skills/duty-scan.md` (morning)
- Requires Orbit MCP tools loaded

## Evening

- Time: `0 18 * * *` (local)
- Same skill with evening framing

## Mail / calendar feed

Prefer Hermes skills over Orbit Graph inbox sync:

1. Open Hermes dashboard (:9119) from Orbit Settings → **Open in Orbit**
2. Connect Google Workspace and/or Outlook skill/MCP
3. During duty scan or event wakes, Hermes should ingest/advise into Orbit tools
4. Keep Orbit Classic Outlook DnD + ICS/COM as offline fallback
