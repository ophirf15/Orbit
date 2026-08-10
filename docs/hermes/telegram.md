# Hermes Telegram + Orbit Core

Hermes owns the Telegram gateway. Orbit does **not** implement a Telegram bot, Bot API client, or second transport.

## Ownership

| Concern | Owner |
|---|---|
| Telegram bot token / gateway / polling or webhook | Hermes |
| Session thread on Telegram | Hermes |
| Orbit tools + CRUD validation + SQLite | Orbit Core Host |
| Desktop “Remote activity” display | Orbit App |

## Point tools at Core

Configure Hermes HTTP tools / plugins to call Orbit Core with the Core API key:

```http
Authorization: Bearer <orbit-core-api-key>
```

Base URL is the Core Host bind (loopback by default from Settings → `CoreHostBaseUrl`). See `docs/hermes/orbit-tools.md` for tool paths.

### Session mirror

After (or as) Hermes starts a Telegram session, upsert Orbit continuity metadata:

```http
POST /v1/conversations/sync
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{
  "channel": "telegram",
  "hermesSessionId": "<hermes-session-id>",
  "hermesSessionKey": "<optional>",
  "title": "optional label",
  "externalThreadId": "<optional telegram thread id>"
}
```

### Mutations with provenance

Every Telegram-driven mutation should include platform provenance so desktop audit/activity can attribute the change:

```http
POST /v1/agent/tools/orbit_create_task
Authorization: Bearer <orbit-core-api-key>
Content-Type: application/json

{
  "title": "Follow up with Maria",
  "projectId": "<guid>",
  "provenance": {
    "actor": "hermes",
    "channel": "telegram",
    "hermesSessionId": "<hermes-session-id>",
    "externalUserId": "<telegram-user-id-if-available>"
  }
}
```

`orbit_update_contact` keeps string `provenance` for contact fact notes; send platform provenance as `requestProvenance` on that tool only.

### Remote activity (desktop)

```http
GET /v1/activity/remote
Authorization: Bearer <orbit-core-api-key>
```

Returns recent `channel=telegram` conversations and `audit_events` whose detail JSON includes telegram provenance.

## Availability

Orbit Core must be reachable and authenticated for Telegram-driven CRUD. If Hermes runs on the same PC and Core is asleep/offline, mutations wait until Core is awake. Do not invent a hidden offline queue or conflict engine in Orbit.

If Hermes later runs on another trusted machine:

- never expose Core unauthenticated on the LAN
- require an API key at minimum
- prefer TLS or a private tunnel/VPN

## Remote-safe capabilities

Telegram may use Orbit-owned read + typed mutation tools (tasks, notes, contacts, links, blockers, suggestions).

Do **not** enable via Telegram:

- Windows shell / process execution
- arbitrary external filesystem mutate (write outside generated root)
- elevated developer / source-modification / build operations

Those elevated developer-mode ops stay documented and gated in later malleability phases; they are not exposed on the remote Telegram tool surface.

## Live verify (manual)

Live Hermes Telegram gateway + bot token verification is environment-specific — tracked in `docs/TODO.md`.
