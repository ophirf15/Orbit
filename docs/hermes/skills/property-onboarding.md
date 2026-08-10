# Skill: Property onboarding

Reusable Hermes procedure for bringing a new property into Orbit. This skill teaches **workflow only** — it does **not** grant OS permissions, shell access, or filesystem mounts.

Call typed Orbit Core tools with the Core Host Bearer key. Prefer tools over inventing SQL.

## Preconditions

- Core Host reachable (`docs/hermes/orbit-tools.md`)
- Target project already exists (or create via normal operator flow / capture)
- Operator confirms utility / vendor details before writing values

## Steps

1. **Confirm project context**

   ```http
   POST /v1/agent/tools/orbit_get_project
   {"id":"<project-id>"}
   ```

2. **Add utility account fields** (once per entity type)

   ```http
   POST /v1/agent/tools/orbit_add_custom_field
   {
     "entityType": "workstream",
     "key": "utility_account_number",
     "fieldType": "text",
     "validation": { "maxLength": 64 },
     "display": { "label": "Utility account number" }
   }
   ```

   Repeat for related keys as needed (`utility_provider` as `choice` with validation.choices, `service_start_date` as `date`).

3. **Populate values** for the workstream / project entity

   ```http
   POST /v1/agent/tools/orbit_set_custom_field_value
   {
     "entityType": "workstream",
     "entityId": "<workstream-id>",
     "fieldKey": "utility_account_number",
     "value": "1234567890"
   }
   ```

4. **Create onboarding tasks**

   ```http
   POST /v1/agent/tools/orbit_create_task
   {
     "title": "Confirm utility account on file",
     "projectId": "<project-id>",
     "provenance": { "actor": "hermes", "channel": "desktop" }
   }
   ```

5. **Optional layout** — if the operator wants a dedicated onboarding view:

   ```http
   POST /v1/agent/tools/orbit_save_layout
   {
     "name": "Property onboarding",
     "schemaJson": "{\"lanes\":[{\"id\":\"utilities\",\"title\":\"Utilities\"},{\"id\":\"vendors\",\"title\":\"Vendors\"}],\"sections\":[{\"id\":\"unresolved\",\"title\":\"Unresolved\"}]}"
   }
   ```

   Then `orbit_apply_layout` with the returned `layout.id`. Use `orbit_revert_layout` if the view is wrong.

## Never do

- Arbitrary shell / process execution
- Write project folders or installed Orbit binaries
- Invent custom SQL or bypass validation
- Use `orbit_dev_*` tools unless Developer Mode is explicitly required for a source change
