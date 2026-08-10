namespace Orbit.Agent.Contracts.Capabilities;

public sealed class CapabilityDescriptor
{
    public required string Id { get; init; }

    public required string Route { get; init; }

    public required string Status { get; init; }

    public string? Notes { get; init; }
}

public static class CapabilityCatalog
{
    public const string StubStatus = "stub";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new() { Id = "projects", Route = "/v1/projects", Status = "partial", Notes = "GET list from SQLite; context via /v1/projects/{id}/context" },
        new() { Id = "workbench", Route = "/v1/workbench", Status = "available", Notes = "Project cells + limbo aggregate" },
        new() { Id = "tasks", Route = "/v1/tasks", Status = "partial", Notes = "Typed create/update via agent tools; capture still creates tasks" },
        new() { Id = "notes", Route = "/v1/notes", Status = "partial", Notes = "POST capture + GET limbo; agent create_note tool" },
        new() { Id = "search", Route = "/v1/search", Status = "available", Notes = "FTS global search; optional focusProjectId / focusMeetingId boost" },
        new() { Id = "evidence", Route = "/v1/evidence/query", Status = "available", Notes = "Structured EIN/W-9 + project status evidence with citations; no LLM required" },
        new() { Id = "contacts", Route = "/v1/contacts", Status = "partial", Notes = "List/detail + UpdateContact; enrichment on email ingest (heuristic signature)" },
        new() { Id = "organizations", Route = "/v1/organizations", Status = "partial", Notes = "Basic org list; hierarchy/reporting deeper in later phases" },
        new() { Id = "links", Route = "/v1/links", Status = "partial", Notes = "orbit_link_entities writes relationships; task-to-task edges via orbit_link_tasks; dedicated /v1/links CRUD later" },
        new() { Id = "task.dependencies", Route = "/v1/agent/tools/orbit_get_task_dependencies", Status = "available", Notes = "Directional task_dependencies (blocks|informs|relates) with expects, readiness monitoring, and inbound-info merge proposals" },
        new() { Id = "files.read", Route = "/v1/files/read", Status = "partial", Notes = "Read limited to attached folders + generated root" },
        new() { Id = "files.search", Route = "/v1/files/search", Status = "available", Notes = "Indexed name/content search" },
        new() { Id = "files.write", Route = "/v1/files/write", Status = "enforced", Notes = "Denied unless under generated root" },
        new() { Id = "project.folders", Route = "/v1/projects/{id}/folders", Status = "available", Notes = "Attach + reindex project folders" },
        new() { Id = "emails.ingest", Route = "/v1/emails/ingest", Status = "available", Notes = "MSG ingest (JSON path or multipart file) + multi-project link; Classic Outlook OOP push from Emails page" },
        new() { Id = "emails.outlookPush", Route = "Orbit.App/Emails", Status = "available", Notes = "Out-of-process Classic Outlook selection → SaveAs .msg → ingest (ADR 0024). In-proc COM add-in experimental." },
        new() { Id = "emails.outlookAddIn", Route = "Orbit.OutlookAddIn", Status = "partial", Notes = "Classic Outlook in-proc ribbon experimental; clr.dll AV on some Outlook builds — prefer emails.outlookPush" },
        new() { Id = "artifacts.create", Route = "/v1/artifacts/generated", Status = "enforced", Notes = "Generated root only" },
        new() { Id = "calendar.context", Route = "/v1/calendar/context", Status = "available", Notes = "Upcoming meetings; ICS + Outlook COM best-effort; Graph stub" },
        new() { Id = "calendar.sync", Route = "/v1/calendar/sync", Status = "available", Notes = "Upsert sources/events + link + attention_score" },
        new() { Id = "calendar.sources", Route = "/v1/calendar/sources", Status = "available", Notes = "List distinguishable mailbox/calendar sources" },
        new() { Id = "context.bundle", Route = "/v1/context/bundle", Status = "available", Notes = "Bounded project/workstream/task evidence pack; extractions project-scoped; meetings from calendar" },
        new() { Id = "suggestions", Route = "/v1/suggestions", Status = "available", Notes = "List pending + accept/reject; heuristic AgentEventWorker" },
        new() { Id = "events", Route = "/v1/events", Status = "available", Notes = "SSE stream + heartbeat" },
        new() { Id = "agent.tools.orbit_get_project", Route = "/v1/agent/tools/orbit_get_project", Status = "available", Notes = "Read-only Hermes tool bridge; Bearer Core API key" },
        new() { Id = "agent.tools.orbit_get_contact", Route = "/v1/agent/tools/orbit_get_contact", Status = "available", Notes = "Read-only Hermes tool bridge" },
        new() { Id = "agent.tools.orbit_search_files", Route = "/v1/agent/tools/orbit_search_files", Status = "available", Notes = "Indexed file search only; no raw FS" },
        new() { Id = "agent.tools.orbit_search", Route = "/v1/agent/tools/orbit_search", Status = "available", Notes = "Global FTS search across graph/files/emails/calendar/conversations" },
        new() { Id = "agent.tools.orbit_answer_with_evidence", Route = "/v1/agent/tools/orbit_answer_with_evidence", Status = "available", Notes = "Structured evidence pack with citations (EIN/W-9, project status)" },
        new() { Id = "agent.tools.orbit_get_related_context", Route = "/v1/agent/tools/orbit_get_related_context", Status = "available", Notes = "GetContextBundle wrapper; project-scoped extractions + meetings" },
        new() { Id = "agent.tools.orbit_get_calendar_context", Route = "/v1/agent/tools/orbit_get_calendar_context", Status = "available", Notes = "Upcoming calendar window with source identity + links" },
        new() { Id = "agent.tools.orbit_create_task", Route = "/v1/agent/tools/orbit_create_task", Status = "available", Notes = "Typed mutation; audited; optional telegram provenance" },
        new() { Id = "agent.tools.orbit_update_task", Route = "/v1/agent/tools/orbit_update_task", Status = "available", Notes = "Typed mutation; audited; optional provenance" },
        new() { Id = "agent.tools.orbit_update_project", Route = "/v1/agent/tools/orbit_update_project", Status = "available", Notes = "Update name/summary/accentColor (#RRGGBB or preset name blue|teal|…); audited" },
        new() { Id = "agent.tools.orbit_get_workbench", Route = "/v1/agent/tools/orbit_get_workbench", Status = "available", Notes = "Root or project-scoped workbench snapshot (cells, accents, open lines)" },
        new() { Id = "agent.tools.orbit_archive_entity", Route = "/v1/agent/tools/orbit_archive_entity", Status = "available", Notes = "Soft-archive project|task|note; audited" },
        new() { Id = "agent.tools.orbit_create_note", Route = "/v1/agent/tools/orbit_create_note", Status = "available", Notes = "Typed capture; may trigger suggestions" },
        new() { Id = "agent.tools.orbit_link_entities", Route = "/v1/agent/tools/orbit_link_entities", Status = "available", Notes = "Writes relationships row; audited; optional provenance" },
        new() { Id = "agent.tools.orbit_update_contact", Route = "/v1/agent/tools/orbit_update_contact", Status = "available", Notes = "Wraps UpdateContact; fact provenance string + requestProvenance object" },
        new() { Id = "agent.tools.orbit_accept_suggestion", Route = "/v1/agent/tools/orbit_accept_suggestion", Status = "available", Notes = "Applies assign_to_project, link_tasks, merge_into_task; audited; optional provenance" },
        new() { Id = "agent.tools.orbit_reject_suggestion", Route = "/v1/agent/tools/orbit_reject_suggestion", Status = "available", Notes = "Dismisses a pending suggestion; audited" },
        new() { Id = "agent.tools.orbit_link_tasks", Route = "/v1/agent/tools/orbit_link_tasks", Status = "available", Notes = "Task dependency edge (blocks|informs|relates) with expects; deduped + cycle-guarded; audited" },
        new() { Id = "agent.tools.orbit_unlink_tasks", Route = "/v1/agent/tools/orbit_unlink_tasks", Status = "available", Notes = "Removes a dependency edge by id; audited" },
        new() { Id = "agent.tools.orbit_get_task_dependencies", Route = "/v1/agent/tools/orbit_get_task_dependencies", Status = "available", Notes = "waitingOn/feeds split with satisfied flags" },
        new() { Id = "agent.tools.orbit_suggest_task_links", Route = "/v1/agent/tools/orbit_suggest_task_links", Status = "available", Notes = "Runs relationship heuristics; returns pending proposals only" },
        new() { Id = "agent.tools.orbit_set_blocker", Route = "/v1/agent/tools/orbit_set_blocker", Status = "available", Notes = "Minimal open blocker create; audited; optional provenance" },
        new() { Id = "schema.custom_fields", Route = "/v1/custom-fields", Status = "available", Notes = "Runtime schema tools scope: list + add/set via agent tools" },
        new() { Id = "schema.layouts", Route = "/v1/layouts", Status = "available", Notes = "Versioned layout/view definitions with apply/revert" },
        new() { Id = "agent.tools.orbit_add_custom_field", Route = "/v1/agent/tools/orbit_add_custom_field", Status = "available", Notes = "Runtime configuration/schema scope; typed text/number/bool/date/choice" },
        new() { Id = "agent.tools.orbit_set_custom_field_value", Route = "/v1/agent/tools/orbit_set_custom_field_value", Status = "available", Notes = "Runtime configuration/schema scope; validates against field definition" },
        new() { Id = "agent.tools.orbit_save_layout", Route = "/v1/agent/tools/orbit_save_layout", Status = "available", Notes = "Runtime configuration/schema scope; appends layout revision" },
        new() { Id = "agent.tools.orbit_apply_layout", Route = "/v1/agent/tools/orbit_apply_layout", Status = "available", Notes = "Runtime configuration/schema scope; sets active layout" },
        new() { Id = "agent.tools.orbit_revert_layout", Route = "/v1/agent/tools/orbit_revert_layout", Status = "available", Notes = "Runtime configuration/schema scope; restores prior revision as new version" },
        new() { Id = "agent.tools.orbit_dev_create_branch", Route = "/v1/agent/tools/orbit_dev_create_branch", Status = "available", Notes = "Developer/source scope; requires DeveloperMode + SourceRepoRoot; Telegram denied unless DeveloperRemoteOverride" },
        new() { Id = "agent.tools.orbit_dev_write_file", Route = "/v1/agent/tools/orbit_dev_write_file", Status = "available", Notes = "Developer/source scope; writes only under SourceRepoRoot; project folders denied" },
        new() { Id = "agent.tools.orbit_dev_build", Route = "/v1/agent/tools/orbit_dev_build", Status = "partial", Notes = "Developer/source scope; optional dotnet build under SourceRepoRoot with timeout" },
        new() { Id = "conversations.sync", Route = "/v1/conversations/sync", Status = "available", Notes = "Upsert desktop/telegram conversation + Hermes session mirror" },
        new() { Id = "activity.remote", Route = "/v1/activity/remote", Status = "available", Notes = "Telegram conversations + audited mutations with telegram provenance" },
        new() { Id = "sync.snapshot", Route = "/v1/sync/snapshot", Status = "available", Notes = "SQLite backup API snapshot into OneDrive OrbitSnapshots; never live DB in cloud" },
        new() { Id = "sync.snapshots", Route = "/v1/sync/snapshots", Status = "available", Notes = "List versioned cloud snapshots" },
        new() { Id = "sync.restore", Route = "/v1/sync/restore", Status = "available", Notes = "Restore snapshot after last-known-good backup; checksum verified" },
        new() { Id = "sync.status", Route = "/v1/sync/status", Status = "available", Notes = "Lineage/reconcile status including conflict banner payload" },
        new() { Id = "diagnostics", Route = "/v1/diagnostics", Status = "available", Notes = "Redacted diagnostics JSON (no API keys, hermes key contents, or email bodies)" },
        new() { Id = "diagnostics.export", Route = "/v1/diagnostics/export", Status = "available", Notes = "Writes redacted JSON or zip under generated/diagnostics" },
    ];
}
