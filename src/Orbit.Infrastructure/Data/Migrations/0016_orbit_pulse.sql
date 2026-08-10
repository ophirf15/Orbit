-- Phase 22: Work Jarvis pulse + orbit ignition roster

PRAGMA foreign_keys = ON;

ALTER TABLE projects ADD COLUMN in_orbit INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_projects_in_orbit
  ON projects(in_orbit)
  WHERE archived_at IS NULL AND in_orbit = 1;

CREATE TABLE IF NOT EXISTS pulse_snapshots (
  id TEXT NOT NULL PRIMARY KEY,
  day_brief TEXT NULL,
  payload_json TEXT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_pulse_snapshots_created
  ON pulse_snapshots(created_at DESC);

CREATE TABLE IF NOT EXISTS orbit_settings (
  key TEXT NOT NULL PRIMARY KEY,
  value TEXT NOT NULL
);
