# ADR 0011 — Hermes HTTP client and Orbit tool bridge

## Status

Accepted (Phase 9)

## Context

Orbit needs a persistent agent runtime. Hermes exposes an OpenAI-compatible HTTP API (default `http://127.0.0.1:8642`) plus optional session/run endpoints. Orbit must not embed Codex/OpenAI auth, must not hand Hermes raw SQLite or arbitrary filesystem access, and must keep chat conversations correlated with Hermes sessions.

## Decision

1. **`IHermesClient` / `HermesHttpClient`** talk to Hermes over HTTP: `GET /health`, `GET /v1/capabilities` (404 = degrade), `POST /api/sessions` when present (else mint a local session id for `X-Hermes-Session-Id` / `X-Hermes-Session-Key`), and `POST /v1/chat/completions` with SSE streaming.
2. **Settings** store base URL in `settings.json` and the API key in the existing sidecar (`HermesApiKeyReference`). UI provides key entry, Test Connection, and remote plain-HTTP warnings via `HermesUrlValidation`.
3. **Conversation mapping** uses migration `0005_hermes_sessions.sql` (`hermes_session_id` / `hermes_session_key` on `conversations`) plus `ConversationStore` for create/resume and message append.
4. **Tool bridge** is Core Host authenticated HTTP: `/v1/agent/tools/orbit_get_project`, `orbit_get_contact`, `orbit_search_files`. Hermes calls Orbit with the Core Bearer API key. No SQL routes and no arbitrary FS tools. Mutations wait for Phase 10.
5. **Tests** use `HttpMessageHandler` fakes; live Docker verification is a machine TODO, not a CI gate.

## Consequences

The App can stream Hermes chat and persist sessions without Host mediation for the Hermes wire protocol. Hermes plugins/tools target Core Host only. Capability catalog lists the read tools. Provider/model auth remains Hermes-side.
