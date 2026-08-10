# Orbit domain model

First-class entities (SQLite schema in Phase 4 — see `docs/decisions/0006-sqlite-graph-migrations.md`). Relationships carry context; do not collapse shared vendors into a single cross-project task.

## Entities

- Project / Property
- Workstream
- Task
- Subtask / checklist item
- Note / capture item
- Blocker / issue
- Person
- Organization
- Contact point (email / phone / etc.)
- Relationship edge
- Email artifact
- Email message participant
- Email extraction / claim / action
- File artifact / file reference
- Calendar account / mailbox / calendar
- Calendar event
- Conversation / message (desktop + Telegram continuity)
- Agent suggestion
- Agent action / audit record
- Dynamic view / layout definition
- Skill / procedure metadata
- Generated artifact

## Relationship example

`MetroFiber -[serves]-> Harbor Court -[workstream]-> Internet Setup`  
and independently  
`MetroFiber -[serves]-> Riverview -[workstream]-> Internet Setup`

## Priority vs attention

- **Priority** — user / business importance
- **Attention score** — temporary system estimate of what is needed soon (e.g. meeting in 90 minutes)

## Capture rule

Preserve original captured user text. Inferred structure is suggestion until the user merges it into authoritative state.
