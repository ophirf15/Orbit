# ADR 0021 — Project home folder + `.orbit` writable island

## Status

Accepted

## Context

Workbench projects (Harbor Court, North Pier, …) need a Cursor-like home folder for indexing and agent context. Full-home write access would risk accidental delete/corruption of user documents. ADR 0008 made attached project folders read-only with writes only under the global generated root.

## Decision

1. Each project may designate **one primary home** folder (`project_folders.is_home = 1`). Additional folders may still be attached as read-only roots.
2. On set-home, Orbit creates `{home}/.orbit/` (with a short README). This is the **only** writable island inside the home tree.
3. `PathGuard.TryResolveWritable` allows the global generated root **or** any active home’s `.orbit` directory. Paths under home but outside `.orbit` remain read-only.
4. `IExternalFileCapability` stays mutation-free. No Hermes tool may delete/rename/move user files under home.
5. File reindex skips open/hash/extract when `size_bytes` + `modified_at` match; if content hash is unchanged, metadata updates keep existing `indexed_text`.

## Consequences

Agents get home path + sandbox path on context bundles (`FileWritePolicy`). UI: project cell **Set home folder…** / **Open home folder**. Extra attaches remain on the Files page.
