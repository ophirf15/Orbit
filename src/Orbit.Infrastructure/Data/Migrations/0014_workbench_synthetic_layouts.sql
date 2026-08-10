-- Persist geometry for synthetic workbench cells (e.g. Limbo).

CREATE TABLE IF NOT EXISTS workbench_synthetic_layouts (
  cell_id TEXT NOT NULL PRIMARY KEY,
  cell_kind TEXT NOT NULL,
  board_x REAL NULL,
  board_y REAL NULL,
  board_w REAL NULL,
  board_h REAL NULL,
  sort_order INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT NOT NULL
);
