-- Suggestion hygiene: create-time dedupe via group_key; pending uniqueness per type+key.

ALTER TABLE agent_suggestions ADD COLUMN group_key TEXT;

CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_suggestions_pending_group
ON agent_suggestions(suggestion_type, group_key)
WHERE status = 'pending' AND archived_at IS NULL AND group_key IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_agent_suggestions_group_key
ON agent_suggestions(group_key)
WHERE group_key IS NOT NULL;
