-- Phase 19: duty operator — standing rules, memory, runs
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS operator_rules (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  trigger_kind TEXT NOT NULL,
  match_json TEXT NULL,
  action_kind TEXT NOT NULL,
  params_json TEXT NULL,
  require_confirm INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_operator_rules_enabled_trigger
  ON operator_rules(enabled, trigger_kind)
  WHERE archived_at IS NULL;

CREATE TABLE IF NOT EXISTS operator_memory (
  id TEXT NOT NULL PRIMARY KEY,
  scope TEXT NOT NULL DEFAULT 'global',
  kind TEXT NOT NULL,
  text TEXT NOT NULL,
  evidence_refs_json TEXT NULL,
  confidence REAL NULL,
  source TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_operator_memory_scope_kind
  ON operator_memory(scope, kind)
  WHERE archived_at IS NULL;

CREATE TABLE IF NOT EXISTS operator_runs (
  id TEXT NOT NULL PRIMARY KEY,
  trigger_kind TEXT NOT NULL,
  trigger_payload_json TEXT NULL,
  hermes_session_id TEXT NULL,
  hermes_run_id TEXT NULL,
  status TEXT NOT NULL,
  briefing_summary TEXT NULL,
  error_text TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  completed_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_operator_runs_created
  ON operator_runs(created_at DESC);
