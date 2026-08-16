#!/usr/bin/env python3
"""Orbit Pulse monitor script for the Hermes `orbit-pulse-monitor` cron job.

Attached as the job's pre-check `script=` (ADR 0028). Hermes runs this before the
agent turn and reads a final stdout line `{"wakeAgent": bool, ...}`:

    https://hermes-agent.nousresearch.com/docs/user-guide/features/cron

Token hygiene (2026-08-12):
  - Hash a **stable semantic surface** only: schema + projects + tasks + meetings
    (id/title). Strip requestId, changeCursor, and attentionScore — those churn
    without meaningful work-graph changes (calendar.synced, clock buckets).
  - Prefer wake when the stable hash changes **or** pulse/delta lists material
    entity changes (task/project / task.updated / operator.briefing).
  - Pure calendar cursor bumps without semantic/material change → wakeAgent false.
  - Core unreachable → wakeAgent false (log error; do not burn an LLM tick).

Env:
  ORBIT_CORE_URL   Base URL for Orbit Core Host, e.g. http://127.0.0.1:8741
  ORBIT_API_KEY    Bearer token for Core Host
  HERMES_HOME      Optional; defaults to ~/.hermes (state file location only)
"""

from __future__ import annotations

import hashlib
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

REQUEST_TIMEOUT_SECONDS = 15
STATE_FILE_NAME = "orbit-pulse-monitor.json"

# Delta rows that justify an LLM tick (ignore calendar.synced / heartbeat churn).
MATERIAL_ENTITY_TYPES = frozenset({"task", "project", "note", "email", "blocker"})
MATERIAL_SOURCE_EVENTS = frozenset(
    {
        "task.updated",
        "task.created",
        "task.moved",
        "note.created",
        "email.ingested",
        "operator.briefing",
        "project.updated",
        "blocker.created",
        "blocker.archived",
    }
)


def hermes_home() -> Path:
    raw = os.environ.get("HERMES_HOME")
    if raw and raw.strip():
        return Path(raw.strip()).expanduser()
    return Path.home() / ".hermes"


def state_path() -> Path:
    return hermes_home() / "state" / STATE_FILE_NAME


def load_state() -> dict[str, Any]:
    path = state_path()
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def save_state(state: dict[str, Any]) -> None:
    path = state_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(state, indent=2, sort_keys=True), encoding="utf-8")
    tmp.replace(path)


def http_get_json(url: str, api_key: str) -> Any:
    request = urllib.request.Request(
        url,
        method="GET",
        headers={
            "Authorization": f"Bearer {api_key}",
            "Accept": "application/json",
        },
    )
    with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
        body = response.read().decode("utf-8")
    return json.loads(body) if body else None


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode("utf-8")


def emit(payload: dict[str, Any]) -> None:
    print(json.dumps(payload, sort_keys=True))


def stable_hash_surface(snapshot: Any) -> dict[str, Any]:
    """Strip volatile fields before hashing (see docs/hermes/orbit-tools.md)."""
    if not isinstance(snapshot, dict):
        return {"raw": snapshot}

    meetings_in = snapshot.get("meetings") or []
    meetings: list[dict[str, Any]] = []
    if isinstance(meetings_in, list):
        for m in meetings_in:
            if not isinstance(m, dict):
                continue
            meetings.append(
                {
                    "id": m.get("id"),
                    "title": m.get("title"),
                    # attentionScore intentionally omitted — rescores with clock.
                }
            )

    return {
        "schema": snapshot.get("schema"),
        "projects": snapshot.get("projects"),
        "tasks": snapshot.get("tasks"),
        "meetings": meetings,
        # changeCursor / requestId omitted — cursor advances on calendar.synced.
    }


def snapshot_hash(snapshot: Any) -> str:
    return hashlib.sha256(canonical_bytes(stable_hash_surface(snapshot))).hexdigest()


def delta_is_material(delta: Any) -> bool:
    if not isinstance(delta, dict):
        return False
    changed = delta.get("changed") or delta.get("events") or []
    if not isinstance(changed, list) or not changed:
        return False
    for row in changed:
        if not isinstance(row, dict):
            continue
        et = str(row.get("entityType") or "").strip().lower()
        src = str(row.get("sourceEvent") or "").strip().lower()
        if et in MATERIAL_ENTITY_TYPES or src in MATERIAL_SOURCE_EVENTS:
            return True
    return False


def main() -> int:
    core_url = (os.environ.get("ORBIT_CORE_URL") or "").strip().rstrip("/")
    api_key = (os.environ.get("ORBIT_API_KEY") or "").strip()

    if not core_url or not api_key:
        emit(
            {
                "wakeAgent": False,
                "error": "missing_env",
                "detail": "ORBIT_CORE_URL and ORBIT_API_KEY must be set for orbit-pulse-monitor.py.",
            }
        )
        return 0

    try:
        snapshot = http_get_json(f"{core_url}/v1/agent/snapshot", api_key)
    except urllib.error.HTTPError as exc:
        emit(
            {
                "wakeAgent": False,
                "error": "http_error",
                "status": exc.code,
                "detail": f"GET /v1/agent/snapshot failed: {exc.reason}",
            }
        )
        return 0
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        emit(
            {
                "wakeAgent": False,
                "error": "unreachable",
                "detail": f"Orbit Core unreachable at {core_url}: {exc}",
            }
        )
        return 0
    except ValueError as exc:
        emit(
            {
                "wakeAgent": False,
                "error": "bad_response",
                "detail": f"Non-JSON snapshot response: {exc}",
            }
        )
        return 0

    delta: Any = None
    state = load_state()
    cursor = state.get("cursor")
    try:
        delta_url = f"{core_url}/v1/pulse/delta"
        if cursor is not None and str(cursor) != "":
            delta_url += f"?cursor={urllib.parse.quote(str(cursor))}"
        delta = http_get_json(delta_url, api_key)
    except Exception:
        delta = None

    current_hash = snapshot_hash(snapshot)
    previous_hash = state.get("snapshotHash")

    next_cursor = None
    if isinstance(delta, dict):
        next_cursor = delta.get("nextCursor")
        if next_cursor is None:
            next_cursor = delta.get("cursor")

    hash_changed = previous_hash is None or current_hash != previous_hash
    material = delta_is_material(delta) if previous_hash is not None else False

    # First run: wake so Hermes baselines. Later: wake only on semantic or material delta.
    should_wake = hash_changed or material

    save_state(
        {
            **state,
            "snapshotHash": current_hash,
            "cursor": next_cursor if next_cursor is not None else cursor,
        }
    )

    if not should_wake:
        emit({"wakeAgent": False, "snapshotHash": current_hash})
        return 0

    context: dict[str, Any] = {"snapshotHash": current_hash, "snapshot": snapshot}
    if delta is not None:
        context["delta"] = delta
    emit({"wakeAgent": True, "context": context})
    return 0


if __name__ == "__main__":
    sys.exit(main())
