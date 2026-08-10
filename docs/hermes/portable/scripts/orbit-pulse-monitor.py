#!/usr/bin/env python3
"""Orbit Pulse monitor script for the Hermes `orbit-pulse-monitor` cron job.

Attached as the job's pre-check `script=` (ADR 0028 / plan 021 unit U4). Hermes
runs this before the agent turn and reads a final stdout line of the form
`{"wakeAgent": bool, ...}` to decide whether to spend an LLM call this tick:

    https://hermes-agent.nousresearch.com/docs/user-guide/features/cron
    (see "Skipping the agent entirely: wakeAgent")

Behavior:
  - Fetch a stable snapshot from Orbit Core (`GET /v1/agent/snapshot`), plus an
    optional pulse delta (`GET /v1/pulse/delta?cursor=...`) once Core exposes it.
  - Hash the canonical snapshot bytes and compare against the last successful
    run's hash, stored under `$HERMES_HOME/state/orbit-pulse-monitor.json`.
  - Unchanged hash -> print `{"wakeAgent": false}` only (no LLM run, no tokens).
  - Changed / first run -> print `{"wakeAgent": true, "context": {...}}` so the
    agent gets the snapshot without re-querying it.
  - Core unreachable / HTTP error -> print a clear error JSON object and leave
    `wakeAgent` unset (defaults to true) so an outage gets reported instead of
    silently swallowed.

Env:
  ORBIT_CORE_URL   Base URL for Orbit Core Host, e.g. http://127.0.0.1:8741
  ORBIT_API_KEY    Bearer token for Core Host
  HERMES_HOME      Optional; defaults to ~/.hermes (state file location only)

No third-party dependencies — stdlib only, so this runs under any Hermes
Python interpreter without a pip install step.
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


def main() -> int:
    core_url = (os.environ.get("ORBIT_CORE_URL") or "").strip().rstrip("/")
    api_key = (os.environ.get("ORBIT_API_KEY") or "").strip()

    if not core_url or not api_key:
        emit(
            {
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
                "error": "http_error",
                "status": exc.code,
                "detail": f"GET /v1/agent/snapshot failed: {exc.reason}",
            }
        )
        return 0
    except (urllib.error.URLError, TimeoutError, OSError) as exc:
        emit(
            {
                "error": "unreachable",
                "detail": f"Orbit Core unreachable at {core_url}: {exc}",
            }
        )
        return 0
    except ValueError as exc:
        emit({"error": "bad_response", "detail": f"Non-JSON snapshot response: {exc}"})
        return 0

    delta: Any = None
    state = load_state()
    cursor = state.get("cursor")
    try:
        delta_url = f"{core_url}/v1/pulse/delta"
        if cursor:
            delta_url += f"?cursor={urllib.parse.quote(str(cursor))}"
        delta = http_get_json(delta_url, api_key)
    except Exception:
        # Optional endpoint; Core may not expose it yet (plan unit U3). Never fatal.
        delta = None

    snapshot_hash = hashlib.sha256(canonical_bytes(snapshot)).hexdigest()
    previous_hash = state.get("snapshotHash")

    next_cursor = None
    if isinstance(delta, dict):
        next_cursor = delta.get("nextCursor") or delta.get("cursor")

    if snapshot_hash == previous_hash:
        save_state({**state, "snapshotHash": snapshot_hash, "cursor": next_cursor or cursor})
        emit({"wakeAgent": False})
        return 0

    save_state({"snapshotHash": snapshot_hash, "cursor": next_cursor or cursor})
    context: dict[str, Any] = {"snapshotHash": snapshot_hash, "snapshot": snapshot}
    if delta is not None:
        context["delta"] = delta
    emit({"wakeAgent": True, "context": context})
    return 0


if __name__ == "__main__":
    sys.exit(main())
