# Orbit v1 acceptance

Maps Phase 18 domain scenario steps to verification status. Automated rows are covered by unit/integration tests or packaging CI. Manual / Blocked-TODO rows need your environment (Hermes, Outlook, OneDrive, signing) — see `docs/TODO.md`.

V1 definition (from `18_HARDENING.md`): Orbit usable daily for real onboarding without Graph/Azure, with Hermes safely reasoning over and CRUDing Orbit-owned state locally and through Telegram.

| # | Scenario step | Status | Pointers |
|---|---|---|---|
| 1 | Create Harbor Court project | Automated | Demo seed / workbench APIs; `GraphContextSeedTests`, `WorkbenchApiTests` |
| 2 | Attach Harbor Court folder (W-9, proposal, pro forma) | Automated | `FileCapabilityTests.AttachIndexSearchAndLinkW9`; Phase 6 file index |
| 3 | Add MetroFiber, PG&E, phone-porting, vendor-contract workstreams | Automated | Demo graph seed workstreams; `ContextBundleTests` / agent project context |
| 4 | Ingest several Outlook emails | Automated (fixture) / Manual (live Outlook) | `EmailIngestionApiTests`, `EmailIngestionServiceTests`; live Classic Outlook DnD → `docs/TODO.md` Phase 07 |
| 5 | Auto-create/enrich contacts from signatures | Automated | `ContactEnrichmentTests`, `ContactApiTests` |
| 6 | Second property with overlapping MetroFiber relationship | Automated | Demo seed Riverview + shared org links; `ContextBundleTests` |
| 7 | Ingest MetroFiber email referring to both properties | Automated | Multi-project claim splitter; `EmailIngestionApiTests` / claim split coverage |
| 8 | Per-claim/project disambiguation | Automated | `ContextBundleTests`, agent `orbit_get_related_context`; LLM ranking still deferred (`docs/TODO.md` Phase 11) |
| 9 | Add/recognize upcoming Harbor Court meeting | Automated (ICS) / Manual (Outlook COM) | `CalendarIntelligenceTests`, calendar sync API; live COM → `docs/TODO.md` Phase 12 |
| 10 | Attention score surfaces Harbor Court without changing explicit priorities | Automated | `CalendarIntelligenceTests` / attention scorer |
| 11 | Hermes `What's our EIN?` shows W-9 evidence | Automated (evidence API) / Manual (live Hermes chat) | `SearchEvidenceApiTests`, `GlobalSearchAndEvidenceTests`, `orbit_answer_with_evidence`; live chat → Phase 09 TODO |
| 12 | `What is blocking MetroFiber at Harbor Court?` correct status/evidence | Automated (evidence/status tools) / Manual (live Hermes) | Evidence + context bundle; live Hermes → Phase 09/10 TODO |
| 13 | Telegram: create follow-up task + update contact | Automated (API/provenance) / Manual (live bot) | `TelegramContinuityApiTests`, `TelegramProvenanceTests`; live gateway → Phase 13 TODO |
| 14 | Desktop sees remote conversation/actions | Automated (activity API) / Manual (live round-trip) | `/v1/activity/remote`, Agent remote activity UI; live → Phase 13 TODO |
| 15 | New custom field/view through Hermes | Automated (tools) / Manual (live Hermes tool call) | `MalleabilityApiTests`; live tools → Phase 10/15 TODO |
| 16 | Generate Orbit-owned summary file | Automated | `HostApiIntegrationTests.FilesWrite_AllowsGeneratedChild`; artifacts under generated root |
| 17 | Original project files unchanged | Automated | `HardeningTests` / `FileCapabilityTests` external mutate 403; PathGuard |
| 18 | Snapshot to OneDrive + restore on second profile | Automated (local sync folder) / Manual (real OneDrive path) | `SnapshotServiceTests` conflict/corrupt/restore; Settings OneDrive folder → Phase 14 TODO |
| 19 | Publish app update + upgrade installed build | Automated (checker/packaging smoke) / Blocked-TODO (signing + store trust) | Phase 17 update tests + workflow; cert/App Installer → Phase 17 TODO |

## Security / recovery proofs (Phase 18)

| Proof | Status | Pointers |
|---|---|---|
| External delete/rename/move/write → 403 | Automated | `HardeningTests`, `FileCapabilityTests` |
| Requests without API key rejected (non-health) | Automated | `HardeningTests.RequestsWithoutApiKey_AreRejected_ExceptHealth` |
| Agent tools allowlisted; unknown → 404 | Automated | `HardeningTests.AgentTools_AreAllowlisted_UnknownReturns404` |
| Mutation writes `audit_events` | Automated | `HardeningTests.AgentMutation_WritesAuditEvent` |
| Injection-like email body does not escalate | Automated | `HardeningTests.InjectionEmailBody_DoesNotGrantCapabilities_ExternalMutateStill403` |
| Snapshot conflict / corrupt / restore | Automated | `SnapshotServiceTests` (Phase 14) |
| Schema migrate + backup | Automated | `SqliteMigratorTests` |
| Diagnostics export redacted | Automated | `HardeningTests.Diagnostics_Export_IsRedacted_AndUnderGeneratedRoot` |

## How to run automated acceptance

```powershell
.\build.ps1 -Test
```

## Remaining human review

All Manual / Blocked-TODO rows above are tracked under **Pipeline 07–18 complete — review queue** in `docs/TODO.md`.
