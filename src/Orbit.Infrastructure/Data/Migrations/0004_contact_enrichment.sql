-- Phase 8: contact fact provenance + lookup indexes

CREATE TABLE IF NOT EXISTS contact_fact_provenance (
  id TEXT NOT NULL PRIMARY KEY,
  entity_type TEXT NOT NULL,
  entity_id TEXT NOT NULL,
  field TEXT NOT NULL,
  value TEXT NOT NULL,
  source_email_id TEXT NULL REFERENCES email_artifacts(id),
  source_kind TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_contact_fact_provenance_entity
  ON contact_fact_provenance(entity_type, entity_id);

CREATE INDEX IF NOT EXISTS ix_contact_fact_provenance_email
  ON contact_fact_provenance(source_email_id);

CREATE INDEX IF NOT EXISTS ix_contact_fact_provenance_field
  ON contact_fact_provenance(entity_type, entity_id, field);

CREATE INDEX IF NOT EXISTS ix_contact_methods_person
  ON contact_methods(person_id);

CREATE INDEX IF NOT EXISTS ix_contact_methods_org
  ON contact_methods(organization_id);

CREATE INDEX IF NOT EXISTS ix_contact_methods_type_value
  ON contact_methods(method_type, value);

CREATE INDEX IF NOT EXISTS ix_people_display_name
  ON people(display_name);

CREATE INDEX IF NOT EXISTS ix_organizations_name
  ON organizations(name);

CREATE INDEX IF NOT EXISTS ix_organization_memberships_person
  ON organization_memberships(person_id);

CREATE INDEX IF NOT EXISTS ix_organization_memberships_org
  ON organization_memberships(organization_id);

CREATE INDEX IF NOT EXISTS ix_email_participants_person
  ON email_participants(person_id);

CREATE INDEX IF NOT EXISTS ix_agent_suggestions_type_status
  ON agent_suggestions(suggestion_type, status);
