# Orbit Ignition

Teach Hermes the orbit from a typed project list: expand scope, ask followups, create projects/concerns with living briefs via Orbit tools. Seed a thin operator dossier when memory is empty.

## Steps

1. Accept a list of project/site names from the operator.
2. For each name: **search → attach → create**. Match existing projects by name/code/alias before calling `orbit_create_project`.
3. If create returns near-duplicate candidates, attach or ask — only use `force=true` after operator confirmation.
4. Set summary from what you know + followups; add aliases the operator names via `orbit_add_project_alias`.
5. Ask focused followups (ownership, phase, who we wait on, mailbox) — one cluster at a time.
6. Seed open concerns only when named; each must have `body` (brief) + `nextAction`.
7. `orbit_remember` durable orbit roster facts.
8. Operator dossier if thin: role/company, briefing style, standing priorities → `orbit_remember` + Hermes lasting memory.
9. End with ranked “what’s in orbit now.”

## Never

- Leave blank briefs.
- Create dozens of filler tasks.
- Skip tools and only chat the roster.
- Hardcode operator-specific site nicknames into skills.
