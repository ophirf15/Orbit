-- Phase 7: email ingestion columns + dedup indexes

ALTER TABLE email_artifacts ADD COLUMN received_at TEXT;
ALTER TABLE email_artifacts ADD COLUMN conversation_id TEXT;
ALTER TABLE email_artifacts ADD COLUMN body_text_path TEXT;
ALTER TABLE email_artifacts ADD COLUMN body_html_path TEXT;
ALTER TABLE email_artifacts ADD COLUMN content_hash TEXT;

CREATE INDEX IF NOT EXISTS ix_email_artifacts_internet_message_id
  ON email_artifacts(internet_message_id);

CREATE INDEX IF NOT EXISTS ix_email_artifacts_content_hash
  ON email_artifacts(content_hash);

CREATE INDEX IF NOT EXISTS ix_email_artifacts_conversation_id
  ON email_artifacts(conversation_id);

CREATE INDEX IF NOT EXISTS ix_email_participants_email
  ON email_participants(email_artifact_id);

CREATE INDEX IF NOT EXISTS ix_email_project_links_email
  ON email_project_links(email_artifact_id);
