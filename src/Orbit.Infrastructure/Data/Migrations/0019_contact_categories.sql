-- Contact categories + resident disposition (ADR 0030 / plan contacts).
-- category: company | client | vendor | NULL (pending)
-- disposition: active | flagged_resident | excluded_resident

ALTER TABLE people ADD COLUMN category TEXT NULL;
ALTER TABLE people ADD COLUMN disposition TEXT NOT NULL DEFAULT 'active';

CREATE INDEX IF NOT EXISTS ix_people_category
  ON people(category)
  WHERE archived_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_people_disposition
  ON people(disposition)
  WHERE archived_at IS NULL;
