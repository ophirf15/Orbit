-- Project aliases: operator-defined nicknames for matching/routing (never product defaults).
-- normalized_alias is globally unique so one nickname cannot point at two projects.

CREATE TABLE IF NOT EXISTS project_aliases (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL REFERENCES projects(id),
  alias TEXT NOT NULL,
  normalized_alias TEXT NOT NULL,
  created_at TEXT NOT NULL,
  UNIQUE(normalized_alias)
);

CREATE INDEX IF NOT EXISTS ix_project_aliases_project
  ON project_aliases(project_id);
