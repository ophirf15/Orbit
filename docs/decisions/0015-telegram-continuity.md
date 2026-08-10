# ADR 0015 — Telegram continuity through Hermes

## Status

Accepted (Phase 13)

## Context

Users need to talk to Hermes from Telegram while away from the desktop, using the same Orbit tools and graph, without Orbit reimplementing Telegram transport or a second bot.

## Decision

1. **Hermes owns Telegram transport** (gateway, bot token, polling/webhook). Orbit never adds a Telegram Bot NuGet or Bot API client.
2. **Conversations** store `channel` (`desktop` \| `telegram`) plus Hermes session id/key. Hermes (or Core tools) upsert mirrors via `POST /v1/conversations/sync`.
3. **Mutation tools** accept optional `provenance` `{ actor, channel, hermesSessionId, externalUserId }` and embed it in `audit_events.detail_json` (as `provenance`, or `platformProvenance` when contact fact `provenance` is already a string).
4. **Desktop** loads `GET /v1/activity/remote` on the Agent page: recent telegram conversations + audited remote mutations; selecting a session opens the mapped Orbit conversation without fabricating a new desktop thread.
5. **Remote-safe only**: no Windows shell and no external FS mutate on the Telegram tool path. Elevated developer-mode ops remain documented/gated elsewhere.
6. **Core must be awake** for Telegram CRUD; live gateway verify stays a manual TODO.

## Consequences

CI simulates telegram sync + provenance + activity. Live Hermes Telegram remains an environment verify item. Continuity is metadata + audit, not a second chat product.
