-- Email→project match provenance + duty-task source attribution (operator data only).

ALTER TABLE email_project_links ADD COLUMN confidence REAL NULL;
ALTER TABLE email_project_links ADD COLUMN match_reason TEXT NULL;

ALTER TABLE email_extractions ADD COLUMN match_reason TEXT NULL;

ALTER TABLE tasks ADD COLUMN source_kind TEXT NULL;
ALTER TABLE tasks ADD COLUMN source_confidence REAL NULL;
ALTER TABLE tasks ADD COLUMN source_match_reason TEXT NULL;
