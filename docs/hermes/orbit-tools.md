# Orbit tools for Hermes

Hermes should call **Orbit Core Host**, not SQLite and not the Windows filesystem.

Default Core Host (loopback): `http://127.0.0.1:<port>` from Orbit Settings (`CoreHostBaseUrl`).

## Authentication

Send the Core Host API key as a Bearer token:

```http
Authorization: Bearer <orbit-core-api-key>
```

The key lives in the Core Host API key sidecar (see Settings → key reference path). Non-loopback Core binds already require a key.

Do **not** give Hermes the SQLite path, migration scripts, or generated-files root as a writable mount for agent tools.

## Read-only tools (Phase 9–12)

| Tool | Method | Path | Inputs | Returns |
|---|---|---|---|---|
| `orbit_get_project` | GET or POST | `/v1/agent/tools/orbit_get_project` | `id` (query or JSON body) | Project row + context bundle |
| `orbit_get_workbench` | GET or POST | `/v1/agent/tools/orbit_get_workbench` | optional `projectId` | Root or project-scoped workbench (cells, accents, open lines) |
| `orbit_get_contact` | GET or POST | `/v1/agent/tools/orbit_get_contact` | `id` (query or JSON body) | Contact detail JSON |
| `orbit_list_contacts` | POST | `/v1/agent/tools/orbit_list_contacts` | optional `category` (`company`\|`client`\|`vendor`\|`pending`), `disposition` (`flagged_resident`), `limit` | Bounded contact list (excludes archived / excluded_resident; category browse hides flagged) |
| `orbit_update_contact` | POST | `/v1/agent/tools/orbit_update_contact` | `id`, `patch`, optional string `provenance` (fact), `requestProvenance` (platform), `actor` | Patch category, disposition, name, title, org, phones, emails, reportsTo |
| `orbit_archive_contact` | POST | `/v1/agent/tools/orbit_archive_contact` | `id`, optional `excludeAsResident`, `provenance`, `actor` | Soft-archive; excludeAsResident → `excluded_resident` |
| `orbit_flag_resident` | POST | `/v1/agent/tools/orbit_flag_resident` | `id` | Shorthand → disposition `flagged_resident` |
| `orbit_search_files` | GET or POST | `/v1/agent/tools/orbit_search_files` | `q` / `query`, optional `projectId` | Indexed search hits (no raw FS) |
| `orbit_search` | GET or POST | `/v1/agent/tools/orbit_search` | `q` / `query`, optional `focusProjectId`, `focusMeetingId` | Global FTS hits across graph/files/emails/calendar/conversations |
| `orbit_answer_with_evidence` | GET or POST | `/v1/agent/tools/orbit_answer_with_evidence` | `q` / `question`, optional `projectId` | Structured evidence pack + citations (EIN/W-9, project status); no LLM |
| `orbit_get_related_context` | GET or POST | `/v1/agent/tools/orbit_get_related_context` | `targetType` (`project`\|`workstream`\|`task`), `targetId`, optional `attentionProjectId` | Bounded `GetContextBundle` (project-scoped extractions + linked calendar meetings) |
| `orbit_get_calendar_context` | GET or POST | `/v1/agent/tools/orbit_get_calendar_context` | optional `days` (default 14), `limit`, `changedSince` (ISO-8601) | Upcoming meetings with organizer, `updatedAt`, source identity + entity links; `changedSince` filters to meetings updated at/after that time |
| `orbit_get_changes` | POST | `/v1/agent/tools/orbit_get_changes` | `cursor`, `limit` | Change-log page + `nextCursor` (ADR 0028) |
| `orbit_get_pulse_delta` | POST | `/v1/agent/tools/orbit_get_pulse_delta` | `cursor`, `limit` | Concern/task deltas since cursor |
| `orbit_list_blocked_tasks` | POST | `/v1/agent/tools/orbit_list_blocked_tasks` | optional `projectId`, `limit` | Bulk blocked tasks |
| `orbit_get_agent_snapshot` | POST | `/v1/agent/tools/orbit_get_agent_snapshot` | (none) | Stable snapshot for Hermes `monitor_script` hashing |

Also HTTP (no agent-tool wrapper required for scripts):

```http
GET /v1/changes?cursor=0
GET /v1/pulse/delta?cursor=0
GET /v1/tasks/blocked
GET /v1/agent/snapshot
```

### Hermes monitor fuel (ADR 0028)

These four are the primary polling surface for `monitor_script`. All are cursor/state based — no volatile
timestamps in `/v1/agent/snapshot`, so two reads with no mutations hash identically.

`GET /v1/changes?cursor=0&limit=200` — change-log page since `cursor` (exclusive), ordered by `revision` ASC:

```json
{
  "cursor": 0,
  "nextCursor": 42,
  "events": [
    {
      "revision": 42,
      "entityType": "task",
      "entityId": "<task-guid>",
      "changeKind": "updated",
      "sourceEvent": "task.updated",
      "tombstone": false,
      "changedFields": null
    }
  ],
  "requestId": "<req-id>"
}
```

Poll again with `cursor=<nextCursor>`; an empty `events` array with `nextCursor === cursor` means no changes.

`GET /v1/pulse/delta?cursor=0&limit=200` — task/project change-log events since `cursor`, plus the full current
concerns list (concerns aren't individually revisioned yet, so the whole compact list rides along):

```json
{
  "cursor": 0,
  "nextCursor": 42,
  "changed": [
    { "revision": 42, "entityType": "task", "entityId": "<task-guid>", "sourceEvent": "task.updated", "tombstone": false }
  ],
  "concerns": [
    { "taskId": "<task-guid>", "projectId": "<project-guid>", "projectName": "Harbor Court", "title": "Call electrician", "status": "blocked", "nextAction": "Schedule walkthrough" }
  ],
  "requestId": "<req-id>"
}
```

`GET /v1/tasks/blocked?projectId=&limit=100` — bulk list of tasks with `status = blocked`:

```json
{
  "tasks": [
    { "taskId": "<task-guid>", "projectId": "<project-guid>", "projectName": "Harbor Court", "title": "Call electrician", "status": "blocked", "nextAction": "Schedule walkthrough", "body": null }
  ],
  "requestId": "<req-id>"
}
```

`GET /v1/agent/snapshot` — stable, canonically-ordered snapshot for hashing (excludes `startsAt`/timestamps):

```json
{
  "schema": "orbit.agent.snapshot.v1",
  "changeCursor": 42,
  "projects": [{ "id": "<project-guid>", "name": "Harbor Court", "status": "active", "inOrbit": true }],
  "tasks": [{ "id": "<task-guid>", "projectId": "<project-guid>", "title": "Call electrician", "status": "blocked", "nextAction": "Schedule walkthrough", "priority": 0, "urgency": -1 }],
  "meetings": [{ "id": "<meeting-guid>", "title": "Vendor walkthrough", "attentionScore": 0.82 }],
  "requestId": "<req-id>"
}
```

`requestId` is per-request and must be excluded before hashing the `snapshot` — hash `schema` + `changeCursor` +
`projects` + `tasks` + `meetings` only. `changeCursor` is the only field that moves without a semantic mutation
being reflected elsewhere in the payload.


### Calendar HTTP

```http
GET /v1/calendar/context?days=14&limit=40&changedSince=2026-08-09T00:00:00Z
POST /v1/calendar/sync
GET /v1/calendar/sources
POST /v1/calendar/sources/subscribe
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"path":"C:\\path\\to\\calendar.ics"}
```

`GET /v1/calendar/context` (typed) returns:

```json
{
  "windowDays": 14,
  "changedSince": null,
  "meetings": [
    {
      "id": "<meeting-guid>",
      "title": "Vendor walkthrough",
      "startsAt": "2026-08-10T15:00:00Z",
      "endsAt": "2026-08-10T15:30:00Z",
      "location": "Site office",
      "attentionScore": 0.82,
      "sourceId": "<source-guid>",
      "sourceName": "Work calendar",
      "mailboxName": "operator@example.com",
      "calendarName": "Calendar",
      "organizer": "vendor@example.com",
      "updatedAt": "2026-08-09T12:00:00Z",
      "linkedEntities": [{ "entityType": "project", "entityId": "<project-guid>", "label": "Harbor Court", "confidence": 0.9 }]
    }
  ],
  "requestId": "<req-id>"
}
```

Pass `changedSince` (ISO-8601) to poll for meetings whose `updatedAt` moved since the last check — cheaper than re-diffing the whole window.

Calendar is read-only. Providers: Classic Outlook COM (best-effort), ICS file/URL, Graph stub (optional future). Attention scores live on `calendar_events` and never rewrite task Priority.

Prefer this (or `orbit_get_related_context`) for agent grounding instead of dumping the DB. Extractions on dual-linked emails are filtered to the target project only. Shared vendors appear under `relatedEntities` without merging task contexts. Linked calendar meetings appear under `meetings`.

### Context bundle HTTP

```http
GET /v1/context/bundle?targetType=project&targetId=<guid>
Authorization: Bearer <orbit-core-api-key>
```

### Examples

```http
GET /v1/agent/tools/orbit_get_project?id=<project-guid>
Authorization: Bearer <orbit-core-api-key>
```

```http
POST /v1/agent/tools/orbit_search_files
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"q":"w9","projectId":"<optional-project-guid>"}
```

```http
POST /v1/agent/tools/orbit_search
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"q":"Harbor Court","focusProjectId":"<optional-project-guid>"}
```

```http
POST /v1/agent/tools/orbit_answer_with_evidence
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"question":"What's our EIN?"}
```

```http
GET /v1/search?q=Harbor Court&focusProjectId=<optional>
GET /v1/evidence/query?q=What%27s%20our%20EIN%3F
```

```http
POST /v1/agent/tools/orbit_get_related_context
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"targetType":"project","targetId":"<project-guid>"}
```

```http
POST /v1/agent/tools/orbit_update_project
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"id":"<project-guid>","accentColor":"blue"}
```

```http
POST /v1/agent/tools/orbit_get_workbench
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"projectId":"<optional-project-guid>"}
```

## Mutation tools (Phase 10)

All mutations are validated in Orbit Core and write `audit_events`. Prefer these over inventing SQL.

Hierarchy: **project → workstreams (sub-areas) → tasks**. There is no nested-project table; use workstreams for FF&E / Internet / leasing-style sub-areas under a project.

| Tool | Method | Path | Inputs | Notes |
|---|---|---|---|---|
| `orbit_create_project` | POST | `/v1/agent/tools/orbit_create_project` | `name`, optional `summary`, `inOrbit` (default **true**) | Creates project and adds it to Pulse orbit |
| `orbit_create_workstream` | POST | `/v1/agent/tools/orbit_create_workstream` | `projectId`, `name`, optional `nextAction`, `actor`, `provenance` | Sub-area under a project |
| `orbit_list_workstreams` | GET or POST | `/v1/agent/tools/orbit_list_workstreams` | `projectId` | Lists sub-areas for a project |
| `orbit_create_task` | POST | `/v1/agent/tools/orbit_create_task` | `title`, `projectId`, optional `workstreamId`, `status`, `nextAction`, `body`, `actor`, `provenance` | Creates Orbit-owned task; nest with `workstreamId` |
| `orbit_update_task` | POST | `/v1/agent/tools/orbit_update_task` | `id`, optional `title`/`status`/`nextAction`/`body`, `actor`, `provenance` | Status must be a known enum |
| `orbit_update_project` | POST | `/v1/agent/tools/orbit_update_project` | `id`, optional `name`/`summary`/`accentColor` | Accent: `#RRGGBB` or preset `blue`/`sky`/`teal`/`green`/`amber`/`rose`/`violet`/`slate`; `default`/`none`/`clear` restores theme stripe |
| `orbit_archive_entity` | POST | `/v1/agent/tools/orbit_archive_entity` | `entityType` (`project`\|`task`\|`note`), `entityId`, optional `actor` | Soft-archives (leaves workbench); project archives child tasks/notes too |
| `orbit_create_note` | POST | `/v1/agent/tools/orbit_create_note` | `text`, optional `projectId`, `provenance` | Limbo when no project; may trigger suggestions |
| `orbit_link_entities` | POST | `/v1/agent/tools/orbit_link_entities` | `sourceType`, `sourceId`, `targetType`, `targetId`, `relationshipType`, optional `projectId`, `provenance` | Inserts `relationships` row |
| `orbit_update_contact` | POST | `/v1/agent/tools/orbit_update_contact` | `id`, `patch` (`displayName`, `title`, `organizationId`/`organizationName`, `email`, `mobile`, `phone`, `category`, `disposition`, `reportsToPersonId`), optional string `provenance` (fact), `requestProvenance` (platform), `actor` | Wraps Host UpdateContact |
| `orbit_archive_contact` | POST | `/v1/agent/tools/orbit_archive_contact` | `id`, optional `excludeAsResident`, `provenance`, `actor` | Soft-archive / exclude resident |
| `orbit_flag_resident` | POST | `/v1/agent/tools/orbit_flag_resident` | `id` | Sets `flagged_resident` for People Review |
| `orbit_accept_suggestion` | POST | `/v1/agent/tools/orbit_accept_suggestion` | `id`, optional `actor`, `provenance` | Applies `assign_to_project`, `link_tasks`, or `merge_into_task` |
| `orbit_reject_suggestion` | POST | `/v1/agent/tools/orbit_reject_suggestion` | `id`, optional `actor` | Dismisses a pending suggestion |
| `orbit_set_blocker` | POST | `/v1/agent/tools/orbit_set_blocker` | `summary`, `projectId` and/or `taskId`, optional `provenance` | Minimal open blocker |

### Suggestions HTTP (UI + tools)

```http
GET /v1/suggestions?status=pending
POST /v1/suggestions/{id}/accept
POST /v1/suggestions/{id}/reject
```

Accept of `assign_to_project` sets `notes.project_id`, clears limbo, creates a task, and never rewrites `original_text`.

## Task relationships

Directional edges in `task_dependencies` (`predecessor_task_id` → `successor_task_id`). The successor is
the task that is waiting.

| Tool | Method | Path | Inputs | Notes |
|---|---|---|---|---|
| `orbit_link_tasks` | POST | `/v1/agent/tools/orbit_link_tasks` | `predecessorTaskId`, `successorTaskId`, optional `dependencyType`, `expects`, `reason`, `confidence`, `evidenceRef`, `actor`, `provenance` | Deduped and cycle-guarded; re-linking enriches the existing edge |
| `orbit_unlink_tasks` | POST | `/v1/agent/tools/orbit_unlink_tasks` | `dependencyId`, optional `actor` | Hard-deletes the edge only; tasks are kept |
| `orbit_get_task_dependencies` | GET/POST | `/v1/agent/tools/orbit_get_task_dependencies` | `taskId` (query) or `id` (body) | Returns `waitingOn[]` and `feeds[]` with a `satisfied` flag per edge |
| `orbit_suggest_task_links` | POST | `/v1/agent/tools/orbit_suggest_task_links` | `id` (taskId) | Runs heuristics; returns pending proposals, creates no links |

`dependencyType` values:

- `blocks` — the successor cannot proceed until the predecessor completes.
- `informs` — the predecessor produces information the successor needs. Set `expects` to that thing
  (e.g. `"number of phone lines"`); Orbit matches inbound email against it.
- `relates` — association only, no ordering. Exempt from the cycle guard.

### Monitoring behaviour

`AgentEventWorker` debounces events (750 ms) and runs heuristics that only ever create **pending
suggestions** — never silent mutations:

| Event | Heuristic | Suggestion type |
|---|---|---|
| `note.created` | limbo project match | `assign_to_project` / `review_limbo` |
| `task.updated` | sibling contingency detection | `link_tasks` |
| `task.updated`, `task.dependency.linked` | gating predecessor now complete | `dependency_ready` |
| `email.ingested` | email text matches a task's `expects` | `merge_into_task` |

Accepting `merge_into_task` **appends** an attributed line to `tasks.body`; it never overwrites what
the user already wrote.

### Mutation example — new project + sub-area + task

```http
POST /v1/agent/tools/orbit_create_project
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"name":"Harbor Court","summary":"Property onboarding","inOrbit":true}
```

```http
POST /v1/agent/tools/orbit_create_workstream
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{"projectId":"<project-guid>","name":"FF&E","nextAction":"Collect vendor list"}
```

```http
POST /v1/agent/tools/orbit_create_task
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{
  "title":"Call electrician",
  "projectId":"<project-guid>",
  "workstreamId":"<optional-workstream-guid>",
  "nextAction":"Schedule walkthrough",
  "body":"Living brief…",
  "actor":"agent",
  "provenance":{
    "actor":"hermes",
    "channel":"telegram",
    "hermesSessionId":"<session>",
    "externalUserId":"<optional>"
  }
}
```

### Mutation example — update task

```http
POST /v1/agent/tools/orbit_update_task
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{
  "id":"<task-guid>",
  "nextAction":"Follow up Thursday",
  "body":"Updated living brief…",
  "actor":"agent"
}
```

## Telegram continuity (Phase 13)

Hermes owns the Telegram gateway. Orbit mirrors sessions and audits provenance — see `docs/hermes/telegram.md`.

```http
POST /v1/conversations/sync
GET /v1/activity/remote
```

## Never expose

- delete/move/rename/overwrite arbitrary external files
- arbitrary SQL
- arbitrary shell / process execution on the Windows host

## Out of scope (later)

- Visual layout designer UI
- Auto-PR to GitHub (create branch ships; PR remains manual)
- Requiring live Hermes for suggestion generation (heuristics ship in Core)

## Runtime schema / layouts (Phase 15)

Capability scope: **runtime configuration/schema** (not developer/source).

| Tool | Method | Path | Inputs | Notes |
|---|---|---|---|---|
| `orbit_add_custom_field` | POST | `/v1/agent/tools/orbit_add_custom_field` | `entityType`, `key`, `fieldType` (`text`\|`number`\|`bool`\|`date`\|`choice`), optional `validation`/`display`, `provenance` | Persists definition; choice requires `validation.choices` |
| `orbit_set_custom_field_value` | POST | `/v1/agent/tools/orbit_set_custom_field_value` | `entityType`, `entityId`, `fieldKey`, `value`, optional `provenance` | Validates against definition |
| `orbit_save_layout` | POST | `/v1/agent/tools/orbit_save_layout` | `name`, `schemaJson` (or `schema`), optional `layoutId` to bump version | Appends revision |
| `orbit_apply_layout` | POST | `/v1/agent/tools/orbit_apply_layout` | `layoutId` | Sets active layout |
| `orbit_revert_layout` | POST | `/v1/agent/tools/orbit_revert_layout` | `layoutId`, optional `toVersion` | Restores prior schema as a new version |

List endpoints:

```http
GET /v1/custom-fields?entityType=workstream
GET /v1/layouts
GET /v1/layouts/{id}
GET /v1/layouts/{id}/revisions
```

### Example — add field

```http
POST /v1/agent/tools/orbit_add_custom_field
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{
  "entityType":"workstream",
  "key":"utility_account_number",
  "fieldType":"text",
  "validation":{"maxLength":64},
  "display":{"label":"Utility account number"}
}
```

Hermes skill docs (no OS perms): `docs/hermes/skills/property-onboarding.md`, `docs/hermes/skills/metrofiber-setup.md`, `docs/hermes/skills/duty-scan.md`.

## Duty operator tools (Phase 19 / ADR 0023)

| Tool | Method | Path | Notes |
|---|---|---|---|
| `orbit_remember` | POST | `/v1/agent/tools/orbit_remember` | Curated memory fact |
| `orbit_report_briefing` | POST | `/v1/agent/tools/orbit_report_briefing` | Persist duty/pulse briefing into Orbit Pulse (`briefing`, optional `triggerKind`) |
| `orbit_forget` | POST | `/v1/agent/tools/orbit_forget` | Archive memory |
| `orbit_list_rules` | POST | `/v1/agent/tools/orbit_list_rules` | Enabled standing rules |
| `orbit_set_rule` | POST | `/v1/agent/tools/orbit_set_rule` | Create standing rule |
| `orbit_list_memory` | POST | `/v1/agent/tools/orbit_list_memory` | List memory |
| `orbit_link_email_thread` | POST | `/v1/agent/tools/orbit_link_email_thread` | Task ↔ conversation |
| `orbit_list_task_emails` | GET/POST | `/v1/agent/tools/orbit_list_task_emails` | Threads for task |
| `orbit_open_email` | POST | `/v1/agent/tools/orbit_open_email` | Resolve `.msg` path |

Also: `GET /v1/operator/runs`, `GET/POST /v1/operator/rules`, `GET /v1/operator/memory`, `POST /v1/suggestions/{id}/always`.

## Developer / source tools (Phase 15)

Capability scope: **developer/source**. Requires Settings → Developer Mode + `SourceRepoRoot`. Telegram (`provenance.channel=telegram`) is **403** unless `DeveloperRemoteOverride` is true. Never writes project folders or installed binaries.

| Tool | Method | Path | Inputs | Notes |
|---|---|---|---|---|
| `orbit_dev_create_branch` | POST | `/v1/agent/tools/orbit_dev_create_branch` | `branchName`, optional `provenance` | `git checkout -b` under SourceRepoRoot |
| `orbit_dev_write_file` | POST | `/v1/agent/tools/orbit_dev_write_file` | `path`, `contents`, optional `provenance` | Path must resolve under SourceRepoRoot |
| `orbit_dev_build` | POST | `/v1/agent/tools/orbit_dev_build` | optional `provenance` | `dotnet build` under SourceRepoRoot with timeout |

## Hermes registration sketch

Prefer the Orbit-owned MCP stdio bridge (`src/Orbit.Mcp`) registered under Hermes `mcp_servers` — portable pack: `docs/hermes/portable/` (snippets, `.env.example`, checklist). Do not install that MCP into Cursor.

Alternatively, point an HTTP tool / plugin at Core Host with the Bearer key, register the read + mutation paths above, and keep Hermes session ids aligned with Orbit conversations via `POST /v1/conversations/sync` (and chat session headers). For Telegram, always stamp mutation `provenance.channel=telegram`.
