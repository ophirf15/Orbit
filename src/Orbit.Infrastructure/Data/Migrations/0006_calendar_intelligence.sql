-- Phase 12: calendar provider identity, event body/uid, link confidence

ALTER TABLE calendar_sources ADD COLUMN external_key TEXT;
ALTER TABLE calendar_sources ADD COLUMN mailbox_name TEXT;
ALTER TABLE calendar_sources ADD COLUMN calendar_name TEXT;
ALTER TABLE calendar_sources ADD COLUMN config_uri TEXT;
ALTER TABLE calendar_sources ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1;
ALTER TABLE calendar_sources ADD COLUMN last_sync_at TEXT;
ALTER TABLE calendar_sources ADD COLUMN last_sync_status TEXT;
ALTER TABLE calendar_sources ADD COLUMN last_sync_error TEXT;

ALTER TABLE calendar_events ADD COLUMN external_uid TEXT;
ALTER TABLE calendar_events ADD COLUMN body_preview TEXT;
ALTER TABLE calendar_events ADD COLUMN organizer TEXT;

ALTER TABLE event_entity_links ADD COLUMN confidence REAL;
ALTER TABLE event_entity_links ADD COLUMN provenance TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_calendar_sources_provider_external
  ON calendar_sources(provider, external_key)
  WHERE external_key IS NOT NULL AND archived_at IS NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_calendar_events_source_uid
  ON calendar_events(calendar_source_id, external_uid)
  WHERE external_uid IS NOT NULL AND archived_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_calendar_events_starts_at
  ON calendar_events(starts_at);

CREATE INDEX IF NOT EXISTS ix_event_entity_links_event
  ON event_entity_links(calendar_event_id);

CREATE INDEX IF NOT EXISTS ix_event_entity_links_entity
  ON event_entity_links(entity_type, entity_id);
