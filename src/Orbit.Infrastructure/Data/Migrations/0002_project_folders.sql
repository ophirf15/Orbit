-- Phase 6: project folder roots + richer file artifact columns

CREATE TABLE IF NOT EXISTS project_folders (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL REFERENCES projects(id),
  root_path TEXT NOT NULL,
  availability TEXT NOT NULL DEFAULT 'available',
  last_indexed_at TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL,
  UNIQUE(project_id, root_path)
);

CREATE INDEX IF NOT EXISTS ix_project_folders_project ON project_folders(project_id);

-- Additive columns (SQLite ignores duplicate ADD on re-apply only if we gate via migrations table).
ALTER TABLE file_artifacts ADD COLUMN extension TEXT;
ALTER TABLE file_artifacts ADD COLUMN modified_at TEXT;
ALTER TABLE file_artifacts ADD COLUMN project_folder_id TEXT;
ALTER TABLE file_artifacts ADD COLUMN availability TEXT;
ALTER TABLE file_artifacts ADD COLUMN indexed_text TEXT;

CREATE INDEX IF NOT EXISTS ix_file_artifacts_folder ON file_artifacts(project_folder_id);
CREATE INDEX IF NOT EXISTS ix_file_artifacts_path ON file_artifacts(path);
