#!/usr/bin/env python3
"""Orbit webhook event filter/dedupe script for Hermes webhook routes.

Attached as a route `script=` (ADR 0028 / plan 021 unit U4/U5), e.g. the
`orbit-email-ingested` route in `webhooks.manifest.json`. Hermes passes the raw
webhook payload as JSON on stdin and inspects stdout:

    https://hermes-agent.nousresearch.com/docs/user-guide/messaging/webhooks
    (see "Script Filters and Transforms")

Outcomes Hermes recognizes:
  - Empty stdout, exact `[SILENT]`, `{"__hermes_ignore__": true}`, a timeout, or
    a nonzero exit code -> the webhook is ignored (HTTP 200, no agent run).
  - JSON object stdout -> replaces the payload used by `prompt` templating.

This script:
  1. Reads the JSON payload from stdin.
  2. Drops junk payloads that are missing an event id (prints `[SILENT]`).
  3. Dedupes by event id against a small state file under
     `$HERMES_HOME/state/orbit-event-ids.json`, capped to the most recent
     `MAX_TRACKED_IDS` ids so the file never grows unbounded.
  4. Duplicate id -> prints `{"__hermes_ignore__": true}`.
  5. Otherwise passes through a small, useful subset of fields as JSON so the
     route prompt template has stable dot-notation fields to reference.

Env:
  HERMES_HOME   Optional; defaults to ~/.hermes (state file location only)

No third-party dependencies — stdlib only.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Any

STATE_FILE_NAME = "orbit-event-ids.json"
MAX_TRACKED_IDS = 500
SILENT = "[SILENT]"

# Fields worth forwarding to the prompt template; everything else in the
# webhook payload is dropped so cron/webhook prompts stay slim (ADR 0028).
PASSTHROUGH_FIELDS = (
    "eventId",
    "eventType",
    "event_type",
    "type",
    "emailId",
    "emailIds",
    "matchedTaskIds",
    "projectIds",
    "conversationId",
    "cursor",
    "receivedAt",
    "subject",
    "from",
)


def hermes_home() -> Path:
    raw = os.environ.get("HERMES_HOME")
    if raw and raw.strip():
        return Path(raw.strip()).expanduser()
    return Path.home() / ".hermes"


def state_path() -> Path:
    return hermes_home() / "state" / STATE_FILE_NAME


def load_seen_ids() -> list[str]:
    path = state_path()
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return []
    ids = data.get("ids") if isinstance(data, dict) else None
    return [str(i) for i in ids] if isinstance(ids, list) else []


def save_seen_ids(ids: list[str]) -> None:
    path = state_path()
    path.parent.mkdir(parents=True, exist_ok=True)
    capped = ids[-MAX_TRACKED_IDS:]
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps({"ids": capped}, indent=2), encoding="utf-8")
    tmp.replace(path)


def read_stdin_payload() -> dict[str, Any] | None:
    raw = sys.stdin.read()
    if not raw or not raw.strip():
        return None
    try:
        payload = json.loads(raw)
    except ValueError:
        return None
    return payload if isinstance(payload, dict) else None


def extract_event_id(payload: dict[str, Any]) -> str | None:
    for key in ("eventId", "event_id", "id"):
        value = payload.get(key)
        if value not in (None, ""):
            return str(value)
    return None


def build_passthrough(payload: dict[str, Any]) -> dict[str, Any]:
    out = {field: payload[field] for field in PASSTHROUGH_FIELDS if field in payload}
    # Hermes route `events:` matches event_type; normalize aliases.
    if "event_type" not in out:
        for key in ("eventType", "type"):
            if key in out and out[key] not in (None, ""):
                out["event_type"] = out[key]
                break
    if "eventType" not in out and "event_type" in out:
        out["eventType"] = out["event_type"]
    if "emailIds" not in out and "emailId" in out:
        out["emailIds"] = [out["emailId"]]
    return out


def main() -> int:
    payload = read_stdin_payload()
    if payload is None:
        print(SILENT)
        return 0

    event_id = extract_event_id(payload)
    if not event_id:
        print(SILENT)
        return 0

    seen = load_seen_ids()
    if event_id in seen:
        print(json.dumps({"__hermes_ignore__": True}))
        return 0

    seen.append(event_id)
    save_seen_ids(seen)

    print(json.dumps(build_passthrough(payload), sort_keys=True))
    return 0


if __name__ == "__main__":
    sys.exit(main())
