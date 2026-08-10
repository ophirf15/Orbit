-- Per-project workbench stripe accent (CSS-style #RRGGBB). NULL = theme default.

ALTER TABLE projects ADD COLUMN accent_color TEXT NULL;
