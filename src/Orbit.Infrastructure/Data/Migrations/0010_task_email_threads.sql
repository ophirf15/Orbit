-- Task ↔ email conversation tracking.
-- conversation_id comes from Outlook/MSG ConversationId (already on email_artifacts).

CREATE TABLE IF NOT EXISTS task_email_threads (
  id TEXT NOT NULL PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(id),
  conversation_id TEXT NOT NULL,
  anchor_email_id TEXT NULL REFERENCES email_artifacts(id),
  linked_by TEXT NOT NULL DEFAULT 'user',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL,
  UNIQUE(task_id, conversation_id)
);

CREATE INDEX IF NOT EXISTS ix_task_email_threads_task
  ON task_email_threads(task_id);

CREATE INDEX IF NOT EXISTS ix_task_email_threads_conversation
  ON task_email_threads(conversation_id);
