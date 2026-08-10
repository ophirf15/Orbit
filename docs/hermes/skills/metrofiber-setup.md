# Skill: MetroFiber setup

Hermes procedure for MetroFiber / ISP onboarding on a property project. Skills grant **no OS permissions**.

## Steps

1. `orbit_get_project` — confirm the property project.
2. `orbit_add_custom_field` on `workstream` (or `project`):
   - `metrofiber_account_number` (`text`)
   - `metrofiber_plan` (`choice` with validation.choices)
   - `install_window` (`date` or text notes via `orbit_create_note`)
3. `orbit_set_custom_field_value` for each confirmed field.
4. `orbit_create_task` — “Schedule MetroFiber install”, “Confirm modem MAC on file”.
5. `orbit_link_entities` — link vendor contact/org when known.
6. Optional: `orbit_save_layout` / `orbit_apply_layout` for an ISP lane view; `orbit_revert_layout` to roll back.

See `property-onboarding.md` for shared guardrails and `docs/hermes/orbit-tools.md` for auth.
