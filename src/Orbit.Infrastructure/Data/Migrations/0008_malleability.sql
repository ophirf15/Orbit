-- Phase 15: malleability — extend Phase 4 placeholder tables + layout revisions
PRAGMA foreign_keys = ON;

-- custom_fields already exists (entity_type, field_key, field_type, label, …)
ALTER TABLE custom_fields ADD COLUMN validation_json TEXT NULL;
ALTER TABLE custom_fields ADD COLUMN display_json TEXT NULL;

CREATE INDEX IF NOT EXISTS ix_custom_fields_entity
  ON custom_fields(entity_type);

CREATE UNIQUE INDEX IF NOT EXISTS ux_custom_field_values_entity_field
  ON custom_field_values(entity_type, entity_id, custom_field_id);

CREATE INDEX IF NOT EXISTS ix_custom_field_values_entity
  ON custom_field_values(entity_type, entity_id);

-- layout_definitions already exists (name, target, definition_json, …)
ALTER TABLE layout_definitions ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE layout_definitions ADD COLUMN is_active INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_layout_definitions_active
  ON layout_definitions(is_active, updated_at);

CREATE TABLE IF NOT EXISTS layout_revisions (
  id TEXT NOT NULL PRIMARY KEY,
  layout_id TEXT NOT NULL REFERENCES layout_definitions(id),
  version INTEGER NOT NULL,
  schema_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE(layout_id, version)
);

CREATE INDEX IF NOT EXISTS ix_layout_revisions_layout
  ON layout_revisions(layout_id, version);
