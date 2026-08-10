-- Change log for Hermes monitor cursors (ADR 0028).
CREATE TABLE IF NOT EXISTS orbit_change_log (
  revision INTEGER PRIMARY KEY AUTOINCREMENT,
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  change_kind TEXT NOT NULL,
  source_event TEXT NULL,
  tombstone INTEGER NOT NULL DEFAULT 0,
  changed_fields_json TEXT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_orbit_change_log_created
  ON orbit_change_log(created_at);

CREATE INDEX IF NOT EXISTS idx_orbit_change_log_entity
  ON orbit_change_log(entity_type, entity_id);
