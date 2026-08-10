# ADR 0005 — Core Host transport and trust boundary

## Status

Accepted (Phase 3)

## Context

Orbit needs a persistent Core Host so the WinUI shell and Hermes share authoritative state when the UI is closed. The phase brief allows named pipes or loopback HTTP for the local UI, and requires HTTP for Hermes (Docker/LAN).

## Decision

- Single **loopback HTTP** API (Kestrel) for both App and Hermes; default bind `127.0.0.1:8741`
- Named pipes deferred (optional later ultra-local transport)
- Bearer API key **mandatory** for non-loopback binds; Host refuses to start without a key sidecar
- Loopback may run without a key; if a key sidecar is present, Bearer is required
- Capability routes are stubs until SQLite (Phase 4); **path writes** are enforced now (generated root only)
- No raw SQL, shell, or arbitrary filesystem write endpoints
- Events: Server-Sent Events at `/v1/events`
- Per-user process with single-instance mutex; not a Windows Service for v1

## Consequences

App launches Host when `backgroundHostEnabled` and health fails. Host outlives App when background mode is on. Hermes (Phase 9) reuses the same HTTP surface with LAN bind + key.
