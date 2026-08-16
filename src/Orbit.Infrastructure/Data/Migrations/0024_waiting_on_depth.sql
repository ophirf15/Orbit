-- Phase 4 waiting-on depth: follow-up, cadence, clear evidence.
-- Choice: extend tasks + task_dependencies (no greenfield table).
-- Tasks already had waiting_on_person_id / waiting_on_organization_id;
-- free-text label covers capture hints like "Grant" without a contact id.
-- Dependencies reuse evidence_ref for satisfy evidence; add satisfied_at + follow-up.

ALTER TABLE tasks ADD COLUMN waiting_on_label TEXT;
ALTER TABLE tasks ADD COLUMN waiting_follow_up_at TEXT;
ALTER TABLE tasks ADD COLUMN waiting_cadence TEXT;
ALTER TABLE tasks ADD COLUMN waiting_satisfied_at TEXT;
ALTER TABLE tasks ADD COLUMN waiting_evidence_ref TEXT;

ALTER TABLE task_dependencies ADD COLUMN follow_up_at TEXT;
ALTER TABLE task_dependencies ADD COLUMN cadence TEXT;
ALTER TABLE task_dependencies ADD COLUMN satisfied_at TEXT;

CREATE INDEX IF NOT EXISTS ix_tasks_waiting_follow_up
  ON tasks(waiting_follow_up_at)
  WHERE waiting_follow_up_at IS NOT NULL
    AND waiting_satisfied_at IS NULL
    AND archived_at IS NULL;
