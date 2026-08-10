-- Hermes session mapping for Orbit conversations (Phase 9)
ALTER TABLE conversations ADD COLUMN hermes_session_id TEXT NULL;
ALTER TABLE conversations ADD COLUMN hermes_session_key TEXT NULL;
CREATE INDEX IF NOT EXISTS ix_conversations_hermes_session ON conversations(hermes_session_id);
