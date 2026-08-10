# ADR 0030 — Person categories and Hermes resident exclusion

## Status

Accepted

## Context

Heuristic contact enrichment (ADR 0010) upserts people from email participants. That created accidental **resident** contacts (e.g. campus housing staff) alongside real company, client, and vendor counterparts. Domain alone cannot decide: a large institutional domain can be an ownership party on deal threads or a resident employer brand.

People UI was browse-only; Hermes lacked list/update/archive contact tools in the MCP catalog.

## Decision

1. **Person-level categories:** `people.category` is `company` | `client` | `vendor` | `NULL` (pending). Org membership still answers “who works for who,” but category can exist before org is clear. Do **not** add `resident` as a browsable category.
2. **Disposition:** `people.disposition` is `active` | `flagged_resident` | `excluded_resident`. Flagged residents appear only in People **Review**. Confirmed residents are soft-archived with `excluded_resident`; re-ingest matching by email does **not** revive them as tracked contacts.
3. **Hermes judges from thread context** (skill `contact-enrich` + SOUL standing rule). Heuristics may create pending stubs; they never auto-classify ambiguous shared domains as company/client.
4. **Mutations:** Host + MCP expose `orbit_list_contacts`, `orbit_update_contact`, `orbit_archive_contact`, `orbit_flag_resident`. People UI: category toggles, edit, remove / confirm-not-tracking.
5. **Institutional / campus brand rule:** ownership vs resident employment is resolved only by message context + standing rules — never email domain alone.

## Consequences

- Migration `0019_contact_categories.sql`.
- After ship: review **Pending** and **Review** queues; purge accidental residents already stored.
- CRM-style org directory redesign stays out of scope; soft-archive only (no hard SQLite delete).
