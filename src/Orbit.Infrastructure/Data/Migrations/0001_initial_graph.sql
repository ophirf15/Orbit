-- Orbit schema v1: domain graph
-- Applied by SqliteMigrator; do not use EnsureCreated.

PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_migrations (
  version TEXT NOT NULL PRIMARY KEY,
  applied_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS projects (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  code TEXT NULL,
  summary TEXT NULL,
  status TEXT NOT NULL DEFAULT 'active',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS workstreams (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL REFERENCES projects(id),
  name TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'active',
  priority INTEGER NULL,
  attention_score REAL NULL,
  next_action TEXT NULL,
  due_at TEXT NULL,
  waiting_on_person_id TEXT NULL,
  waiting_on_organization_id TEXT NULL,
  blocker_summary TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_workstreams_project ON workstreams(project_id);

CREATE TABLE IF NOT EXISTS tasks (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NOT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  title TEXT NOT NULL,
  body TEXT NULL,
  status TEXT NOT NULL DEFAULT 'not_started',
  priority INTEGER NULL,
  attention_score REAL NULL,
  next_action TEXT NULL,
  due_at TEXT NULL,
  waiting_on_person_id TEXT NULL,
  waiting_on_organization_id TEXT NULL,
  blocker_summary TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_tasks_project ON tasks(project_id);
CREATE INDEX IF NOT EXISTS ix_tasks_workstream ON tasks(workstream_id);
CREATE INDEX IF NOT EXISTS ix_tasks_status ON tasks(status);

CREATE TABLE IF NOT EXISTS task_dependencies (
  id TEXT NOT NULL PRIMARY KEY,
  predecessor_task_id TEXT NOT NULL REFERENCES tasks(id),
  successor_task_id TEXT NOT NULL REFERENCES tasks(id),
  dependency_type TEXT NOT NULL DEFAULT 'blocks',
  created_at TEXT NOT NULL,
  UNIQUE(predecessor_task_id, successor_task_id, dependency_type)
);

CREATE TABLE IF NOT EXISTS notes (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  task_id TEXT NULL REFERENCES tasks(id),
  original_text TEXT NOT NULL,
  is_limbo INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_notes_limbo ON notes(is_limbo);

CREATE TABLE IF NOT EXISTS blockers (
  id TEXT NOT NULL PRIMARY KEY,
  project_id TEXT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  task_id TEXT NULL REFERENCES tasks(id),
  summary TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'open',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS people (
  id TEXT NOT NULL PRIMARY KEY,
  display_name TEXT NOT NULL,
  given_name TEXT NULL,
  family_name TEXT NULL,
  notes TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS organizations (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  kind TEXT NULL,
  notes TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS contact_methods (
  id TEXT NOT NULL PRIMARY KEY,
  person_id TEXT NULL REFERENCES people(id),
  organization_id TEXT NULL REFERENCES organizations(id),
  method_type TEXT NOT NULL,
  value TEXT NOT NULL,
  label TEXT NULL,
  is_primary INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS organization_memberships (
  id TEXT NOT NULL PRIMARY KEY,
  person_id TEXT NOT NULL REFERENCES people(id),
  organization_id TEXT NOT NULL REFERENCES organizations(id),
  title TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL,
  UNIQUE(person_id, organization_id)
);

CREATE TABLE IF NOT EXISTS reporting_relationships (
  id TEXT NOT NULL PRIMARY KEY,
  person_id TEXT NOT NULL REFERENCES people(id),
  reports_to_person_id TEXT NOT NULL REFERENCES people(id),
  organization_id TEXT NULL REFERENCES organizations(id),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

-- Context-aware typed graph edges (critical for cross-project isolation)
CREATE TABLE IF NOT EXISTS relationships (
  id TEXT NOT NULL PRIMARY KEY,
  source_type TEXT NOT NULL,
  source_id TEXT NOT NULL,
  target_type TEXT NOT NULL,
  target_id TEXT NOT NULL,
  relationship_type TEXT NOT NULL,
  project_id TEXT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  task_id TEXT NULL REFERENCES tasks(id),
  evidence_ref TEXT NULL,
  confidence REAL NULL,
  confirmed_by_user INTEGER NOT NULL DEFAULT 0,
  created_by TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE INDEX IF NOT EXISTS ix_relationships_source ON relationships(source_type, source_id);
CREATE INDEX IF NOT EXISTS ix_relationships_target ON relationships(target_type, target_id);
CREATE INDEX IF NOT EXISTS ix_relationships_project ON relationships(project_id);
CREATE INDEX IF NOT EXISTS ix_relationships_type ON relationships(relationship_type);

CREATE TABLE IF NOT EXISTS email_artifacts (
  id TEXT NOT NULL PRIMARY KEY,
  subject TEXT NULL,
  sent_at TEXT NULL,
  internet_message_id TEXT NULL,
  body_preview TEXT NULL,
  raw_path TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS email_participants (
  id TEXT NOT NULL PRIMARY KEY,
  email_artifact_id TEXT NOT NULL REFERENCES email_artifacts(id),
  role TEXT NOT NULL,
  address TEXT NOT NULL,
  display_name TEXT NULL,
  person_id TEXT NULL REFERENCES people(id),
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS email_project_links (
  id TEXT NOT NULL PRIMARY KEY,
  email_artifact_id TEXT NOT NULL REFERENCES email_artifacts(id),
  project_id TEXT NOT NULL REFERENCES projects(id),
  created_at TEXT NOT NULL,
  UNIQUE(email_artifact_id, project_id)
);

CREATE TABLE IF NOT EXISTS email_extractions (
  id TEXT NOT NULL PRIMARY KEY,
  email_artifact_id TEXT NOT NULL REFERENCES email_artifacts(id),
  extraction_type TEXT NOT NULL,
  summary TEXT NOT NULL,
  project_id TEXT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  task_id TEXT NULL REFERENCES tasks(id),
  confidence REAL NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS file_artifacts (
  id TEXT NOT NULL PRIMARY KEY,
  path TEXT NOT NULL,
  display_name TEXT NULL,
  content_hash TEXT NULL,
  mime_type TEXT NULL,
  size_bytes INTEGER NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS file_project_links (
  id TEXT NOT NULL PRIMARY KEY,
  file_artifact_id TEXT NOT NULL REFERENCES file_artifacts(id),
  project_id TEXT NOT NULL REFERENCES projects(id),
  created_at TEXT NOT NULL,
  UNIQUE(file_artifact_id, project_id)
);

CREATE TABLE IF NOT EXISTS file_entity_links (
  id TEXT NOT NULL PRIMARY KEY,
  file_artifact_id TEXT NOT NULL REFERENCES file_artifacts(id),
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS calendar_sources (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  provider TEXT NULL,
  account_hint TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS calendar_events (
  id TEXT NOT NULL PRIMARY KEY,
  calendar_source_id TEXT NULL REFERENCES calendar_sources(id),
  title TEXT NOT NULL,
  starts_at TEXT NULL,
  ends_at TEXT NULL,
  location TEXT NULL,
  attention_score REAL NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS event_entity_links (
  id TEXT NOT NULL PRIMARY KEY,
  calendar_event_id TEXT NOT NULL REFERENCES calendar_events(id),
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS conversations (
  id TEXT NOT NULL PRIMARY KEY,
  channel TEXT NOT NULL,
  title TEXT NULL,
  external_thread_id TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS conversation_messages (
  id TEXT NOT NULL PRIMARY KEY,
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  role TEXT NOT NULL,
  body TEXT NOT NULL,
  sent_at TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_conversation_messages_conversation ON conversation_messages(conversation_id);

CREATE TABLE IF NOT EXISTS agent_suggestions (
  id TEXT NOT NULL PRIMARY KEY,
  suggestion_type TEXT NOT NULL,
  summary TEXT NOT NULL,
  payload_json TEXT NULL,
  project_id TEXT NULL REFERENCES projects(id),
  workstream_id TEXT NULL REFERENCES workstreams(id),
  task_id TEXT NULL REFERENCES tasks(id),
  note_id TEXT NULL REFERENCES notes(id),
  status TEXT NOT NULL DEFAULT 'pending',
  confidence REAL NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS agent_actions (
  id TEXT NOT NULL PRIMARY KEY,
  action_type TEXT NOT NULL,
  summary TEXT NOT NULL,
  actor TEXT NOT NULL,
  correlation_id TEXT NULL,
  payload_json TEXT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS audit_events (
  id TEXT NOT NULL PRIMARY KEY,
  event_type TEXT NOT NULL,
  entity_type TEXT NULL,
  entity_id TEXT NULL,
  actor TEXT NOT NULL,
  correlation_id TEXT NULL,
  detail_json TEXT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_audit_events_created ON audit_events(created_at);

CREATE TABLE IF NOT EXISTS dynamic_schemas (
  id TEXT NOT NULL PRIMARY KEY,
  entity_type TEXT NOT NULL,
  name TEXT NOT NULL,
  definition_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS custom_fields (
  id TEXT NOT NULL PRIMARY KEY,
  dynamic_schema_id TEXT NULL REFERENCES dynamic_schemas(id),
  entity_type TEXT NOT NULL,
  field_key TEXT NOT NULL,
  field_type TEXT NOT NULL,
  label TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL,
  UNIQUE(entity_type, field_key)
);

CREATE TABLE IF NOT EXISTS custom_field_values (
  id TEXT NOT NULL PRIMARY KEY,
  custom_field_id TEXT NOT NULL REFERENCES custom_fields(id),
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  value_text TEXT NULL,
  value_number REAL NULL,
  value_json TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS views (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  entity_type TEXT NULL,
  definition_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS layout_definitions (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  target TEXT NOT NULL,
  definition_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS skills_metadata (
  id TEXT NOT NULL PRIMARY KEY,
  name TEXT NOT NULL,
  description TEXT NULL,
  definition_json TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS generated_artifacts (
  id TEXT NOT NULL PRIMARY KEY,
  path TEXT NOT NULL,
  title TEXT NULL,
  artifact_type TEXT NULL,
  project_id TEXT NULL REFERENCES projects(id),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL
);

CREATE TABLE IF NOT EXISTS sync_snapshots (
  id TEXT NOT NULL PRIMARY KEY,
  snapshot_path TEXT NOT NULL,
  device_label TEXT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS device_state (
  id TEXT NOT NULL PRIMARY KEY,
  device_label TEXT NOT NULL,
  last_seen_at TEXT NOT NULL,
  state_json TEXT NULL,
  updated_at TEXT NOT NULL
);

-- Rebuildable search projection (+ FTS5)
CREATE TABLE IF NOT EXISTS search_documents (
  id TEXT NOT NULL PRIMARY KEY,
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  project_id TEXT NULL,
  title TEXT NOT NULL,
  body TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL,
  UNIQUE(entity_type, entity_id)
);

CREATE VIRTUAL TABLE IF NOT EXISTS search_documents_fts USING fts5(
  title,
  body,
  content='search_documents',
  content_rowid='rowid'
);
