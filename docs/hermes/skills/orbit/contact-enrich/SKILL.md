---
name: contact-enrich
description: "Classify and enrich Orbit people from email context; never keep residents."
version: 0.1.0
author: Orbit
license: MIT
platforms: [windows, linux, macos]
metadata:
  hermes:
    tags: [Orbit, Work-Jarvis, Contacts]
    category: orbit
---
# Contact enrich (Orbit)

Run after mail ingest (`email.ingested` / `contact.observed`) when new or pending people appear.

## Purpose

1. For each **material** participant, set person **category** (`company` | `client` | `vendor`) **or** flag/exclude as resident.
2. Extract **title**, **phone(s)**, **organization**, and **reports-to** from body/signature when evident.
3. Persist via Orbit MCP only — never invent tracked contacts for residents.

## Hard policy — no residents

- Orbit does **not** keep residents as browsable contacts.
- If someone is a resident (tenant/occupant / on-site housing staff on a resident thread, etc.) → `orbit_flag_resident` or `orbit_archive_contact` with `excludeAsResident=true`.
- Participants may remain on the **email artifact**; they must not stay as active tracked People.
- Re-ingest must not revive `excluded_resident` / archived people as active tracked contacts (Host enforces).

## Institutional / campus brand standing rule

The same employer or landlord brand can mean **ownership / ops counterpart** or **resident employment**. **Never classify by domain alone.**

| Signal in thread | Action |
|---|---|
| Brand appears as **ownership**, deal, or property counterparty | Likely `client` (or keep pending until clear) — only people who are the ownership/ops counterparts you work with |
| Brand appears as **resident** / resident services / housing staff on a resident thread | `flagged_resident` → confirm exclude; do **not** add every person on that domain as Client |
| Ambiguous | Leave `category` pending; optionally `orbit_flag_resident` for human Review queue |

Operator company domains (Settings / memory) are **hints** for `company` — never auto-map a large institutional domain → company.

## Organization names (canonicalize, don’t duplicate)

People browse by org name. Domain inference and Hermes writes often create near-duplicates (short abbreviation vs full legal/brand name).

When reviewing contacts (mail enrich or “review all contacts”):

1. List people / note distinct `organizationName` values.
2. If two names clearly mean the **same** org (abbreviation, truncated brand, same email domain family), pick the **clearest full name** already in use or evidenced in signatures/mail.
3. Move people with `orbit_update_contact` `organizationName=<canonical>` (and keep title/phones/category).
4. Do **not** hardcode a fixed rename table — decide from thread context + existing Orbit names. When unsure, leave as-is or ask once in briefing.

## Tools

- `orbit_open_email` / `orbit_search` — read thread context
- `orbit_list_contacts` — `category=pending` or `disposition=flagged_resident`
- `orbit_get_contact` — current card
- `orbit_update_contact` — patch: `category`, `disposition`, `displayName`, `title`, `organizationName`, `mobile`/`phone`, `email`, `reportsToPersonId`
- `orbit_flag_resident` — shorthand → `flagged_resident`
- `orbit_archive_contact` — soft-archive; `excludeAsResident=true` for confirmed non-tracking
- `orbit_report_briefing` — only if material (new classifications worth the operator’s attention); else `[SILENT]`

## Steps

1. Open the ingested email; list material From/To/Cc people (skip noise lists, noreply).
2. For each person id from enrichment / search: decide category **or** resident flag from **message context + standing rules**, not domain alone.
3. Write title/phones/org/reporting when signature or body is clear — **phones are required when visible**: call `orbit_update_contact` with `mobile` and/or `phone` in the patch (do not only update category/title). Leave category pending when unsure.
4. If org names collide (abbrev vs full), canonicalize as in **Organization names** above.
5. **Always** finish with `orbit_report_briefing` (summary or `briefing="[SILENT]"`) so Orbit’s push banner can complete. Do not only reply `[SILENT]` in chat.

## Prompt seed (webhook / wake)

```text
Orbit ingested email {emailId}. Run contact-enrich: classify pending people
(company|client|vendor) or flag/exclude residents. Institutional/campus brands:
ownership vs resident employment — never domain-alone. Canonicalize duplicate org
names (abbrev vs full) via organizationName — no fixed rename table. Use
orbit_update_contact / orbit_flag_resident / orbit_archive_contact. Reply [SILENT]
if nothing material.
```
