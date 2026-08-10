-- Primary home folder per project + Orbit-owned .orbit sandbox (writable island).

ALTER TABLE project_folders ADD COLUMN is_home INTEGER NOT NULL DEFAULT 0;

CREATE INDEX IF NOT EXISTS ix_project_folders_home
  ON project_folders(project_id, is_home);
