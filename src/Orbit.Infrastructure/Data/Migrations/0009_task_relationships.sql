-- Task-to-task dependency metadata. The task_dependencies table shipped in 0001
-- (predecessor/successor/dependency_type + UNIQUE) but carried no provenance,
-- no reason, and no reverse index. Unlink is a hard delete so the UNIQUE
-- constraint stays re-linkable.

ALTER TABLE task_dependencies ADD COLUMN reason TEXT;
ALTER TABLE task_dependencies ADD COLUMN expects TEXT;
ALTER TABLE task_dependencies ADD COLUMN confidence REAL;
ALTER TABLE task_dependencies ADD COLUMN evidence_ref TEXT;
ALTER TABLE task_dependencies ADD COLUMN created_by TEXT NOT NULL DEFAULT 'agent';
ALTER TABLE task_dependencies ADD COLUMN updated_at TEXT;

-- UNIQUE(predecessor, successor, type) only indexes the leading column, so
-- reverse lookups ("what blocks this task?") table-scanned.
CREATE INDEX IF NOT EXISTS ix_task_dependencies_successor
  ON task_dependencies(successor_task_id);

CREATE INDEX IF NOT EXISTS ix_task_dependencies_predecessor
  ON task_dependencies(predecessor_task_id);

CREATE INDEX IF NOT EXISTS ix_agent_suggestions_task
  ON agent_suggestions(task_id, suggestion_type, status);
