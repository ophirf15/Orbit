-- Phase 13: Telegram continuity indexes (channel listing + audit provenance queries use detail_json)
CREATE INDEX IF NOT EXISTS ix_conversations_channel_updated ON conversations(channel, updated_at);
