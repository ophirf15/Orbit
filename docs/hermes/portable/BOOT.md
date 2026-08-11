# BOOT — Orbit recovery (ADR 0028)

On gateway start:

1. Confirm Orbit MCP tools respond (`mcp_orbit_orbit_get_workbench` or search).
2. If Core was unreachable while down, run one bounded pulse catch-up via Orbit tools.
3. If catch-up produced a material briefing, run **briefing-distill** (standing truths only).
4. Do **not** start a full duty lecture. If nothing to fix, reply with only `[SILENT]`.
