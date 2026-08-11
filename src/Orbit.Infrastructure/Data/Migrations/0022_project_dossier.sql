-- Thin first-class project dossier (versioned JSON). Operator data only — never product defaults.

ALTER TABLE projects ADD COLUMN dossier_json TEXT;
