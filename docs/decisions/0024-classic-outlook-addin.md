# ADR 0024 — Classic Outlook ingest client

## Status

Accepted (amended)

## Context

Hermes Docker mail skills and Graph (Entra client ID) are blocked or awkward for the operator. Classic Outlook is the daily mail client. Drag/drop `.msg` works (ADR 0009) but does not feel first-class. Users want a one-click push of selected mail into Orbit for the duty operator.

An in-proc .NET Framework COM add-in (`Orbit.OutlookAddIn`) was attempted. On Outlook 16.0.20228 it produced `clr.dll` access violations (0xC0000005 / 0x80131506) during load. Out-of-process Outlook COM (already used for calendar) is stable.

## Decision

1. **Supported path:** Orbit App **Emails → Push selected from Outlook** — out-of-process COM attaches to running Classic Outlook, `SaveAs` `.msg`, then `POST /v1/emails/ingest` (same Host path as DnD).
2. **In-proc ribbon add-in** (`Orbit.OutlookAddIn`, `scripts/register-outlook-addin.ps1`) remains in-tree as **experimental**; do not register by default until a stable loader (e.g. native shim / VSTO) is proven on current Outlook builds.
3. **Auth/config:** App uses existing Core Host client + sidecars. Dedup/enrichment/`email.ingested` → OperatorWakeService unchanged.
4. **Hermes** does not read Outlook; it reasons after ingest.
5. **Drag/drop remains fallback** (ADR 0009). Graph remains future when IT issues a public client ID.

## Consequences

- First-class push without Entra and without crashing Outlook.
- UX is two-step (select in Outlook, click in Orbit) vs ribbon; acceptable until a stable in-proc surface exists.
- Experimental add-in registration must stay off on machines that hit the clr.dll AV.
