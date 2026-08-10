namespace Orbit.Core.Host;

public static class HostEndpoints
{
    public const string Health = "/v1/health";
    public const string Version = "/v1/version";
    public const string Capabilities = "/v1/capabilities";
    public const string Events = "/v1/events";
    public const string Changes = "/v1/changes";

    public const string Projects = "/v1/projects";
    public const string ProjectById = "/v1/projects/{id}";
    public const string ProjectContext = "/v1/projects/{id}/context";
    public const string ProjectAccent = "/v1/projects/{id}/accent";
    public const string WorkbenchCellLayout = "/v1/workbench/cells/{id}/layout";
    public const string ContextBundle = "/v1/context/bundle";
    public const string Workbench = "/v1/workbench";
    public const string Tasks = "/v1/tasks";
    public const string TaskById = "/v1/tasks/{id}";
    public const string TasksBlocked = "/v1/tasks/blocked";
    public const string Notes = "/v1/notes";
    public const string NoteById = "/v1/notes/{id}";
    public const string LimboNoteById = "/v1/notes/limbo/{id}";
    public const string Search = "/v1/search";
    public const string EvidenceQuery = "/v1/evidence/query";
    public const string Contacts = "/v1/contacts";
    public const string Organizations = "/v1/organizations";
    public const string Links = "/v1/links";
    public const string FilesRead = "/v1/files/read";
    public const string FilesWrite = "/v1/files/write";
    public const string FilesSearch = "/v1/files/search";
    public const string EmailsIngest = "/v1/emails/ingest";
    public const string EmailById = "/v1/emails/{id}";
    public const string EmailProjects = "/v1/emails/{id}/projects";
    public const string EmailOpen = "/v1/emails/{id}/open";
    public const string TaskEmailThreads = "/v1/tasks/{taskId}/email-threads";
    public const string Artifacts = "/v1/artifacts/generated";
    public const string Calendar = "/v1/calendar/context";
    public const string CalendarSync = "/v1/calendar/sync";
    public const string CalendarSources = "/v1/calendar/sources";
    public const string CalendarSubscribe = "/v1/calendar/sources/subscribe";
    public const string Suggestions = "/v1/suggestions";
    public const string ConversationsSync = "/v1/conversations/sync";
    public const string ActivityRemote = "/v1/activity/remote";

    public const string SyncSnapshot = "/v1/sync/snapshot";
    public const string SyncSnapshots = "/v1/sync/snapshots";
    public const string SyncRestore = "/v1/sync/restore";
    public const string SyncStatus = "/v1/sync/status";

    public const string Diagnostics = "/v1/diagnostics";
    public const string DiagnosticsExport = "/v1/diagnostics/export";

    public const string OperatorRuns = "/v1/operator/runs";
    public const string OperatorWake = "/v1/operator/wake";
    public const string OperatorRules = "/v1/operator/rules";
    public const string OperatorMemory = "/v1/operator/memory";
    public const string OperatorMemoryRemember = "/v1/operator/memory/remember";

    public const string PulseGet = "/v1/pulse";
    public const string PulseRefresh = "/v1/pulse/refresh";
    public const string PulseDelta = "/v1/pulse/delta";
    public const string ConcernById = "/v1/concerns/{id}";
    public const string OrbitGet = "/v1/orbit";
    public const string OrbitIgnitionFromList = "/v1/orbit/ignition/from-list";
    public const string OrbitIgnitionFromProjectsRoot = "/v1/orbit/ignition/from-projects-root";
    public const string OrbitIgnitionConfirm = "/v1/orbit/ignition/confirm";

    public const string CustomFields = "/v1/custom-fields";
    public const string CustomFieldValues = "/v1/custom-fields/values";
    public const string Layouts = "/v1/layouts";
    public const string LayoutById = "/v1/layouts/{id}";
    public const string LayoutRevisions = "/v1/layouts/{id}/revisions";

    public const string ArchiveEntity = "/v1/archive";

    public const string AgentToolGetProject = "/v1/agent/tools/orbit_get_project";
    public const string AgentToolGetContact = "/v1/agent/tools/orbit_get_contact";
    public const string AgentToolSearchFiles = "/v1/agent/tools/orbit_search_files";
    public const string AgentToolSearch = "/v1/agent/tools/orbit_search";
    public const string AgentToolAnswerWithEvidence = "/v1/agent/tools/orbit_answer_with_evidence";
    public const string AgentToolGetRelatedContext = "/v1/agent/tools/orbit_get_related_context";
    public const string AgentToolGetCalendarContext = "/v1/agent/tools/orbit_get_calendar_context";
    public const string AgentToolCreateTask = "/v1/agent/tools/orbit_create_task";
    public const string AgentToolUpdateTask = "/v1/agent/tools/orbit_update_task";
    public const string AgentToolCreateProject = "/v1/agent/tools/orbit_create_project";
    public const string AgentToolUpdateProject = "/v1/agent/tools/orbit_update_project";
    public const string AgentToolCreateWorkstream = "/v1/agent/tools/orbit_create_workstream";
    public const string AgentToolListWorkstreams = "/v1/agent/tools/orbit_list_workstreams";
    public const string AgentToolGetWorkbench = "/v1/agent/tools/orbit_get_workbench";
    public const string AgentToolCreateNote = "/v1/agent/tools/orbit_create_note";
    public const string AgentToolLinkEntities = "/v1/agent/tools/orbit_link_entities";
    public const string AgentToolUpdateContact = "/v1/agent/tools/orbit_update_contact";
    public const string AgentToolListContacts = "/v1/agent/tools/orbit_list_contacts";
    public const string AgentToolArchiveContact = "/v1/agent/tools/orbit_archive_contact";
    public const string AgentToolFlagResident = "/v1/agent/tools/orbit_flag_resident";
    public const string AgentToolAcceptSuggestion = "/v1/agent/tools/orbit_accept_suggestion";
    public const string AgentToolSetBlocker = "/v1/agent/tools/orbit_set_blocker";
    public const string AgentToolArchiveEntity = "/v1/agent/tools/orbit_archive_entity";
    public const string AgentToolLinkTasks = "/v1/agent/tools/orbit_link_tasks";
    public const string AgentToolUnlinkTasks = "/v1/agent/tools/orbit_unlink_tasks";
    public const string AgentToolGetTaskDependencies = "/v1/agent/tools/orbit_get_task_dependencies";
    public const string AgentToolSuggestTaskLinks = "/v1/agent/tools/orbit_suggest_task_links";
    public const string AgentToolRejectSuggestion = "/v1/agent/tools/orbit_reject_suggestion";

    public const string AgentToolRemember = "/v1/agent/tools/orbit_remember";
    public const string AgentToolForget = "/v1/agent/tools/orbit_forget";
    public const string AgentToolListRules = "/v1/agent/tools/orbit_list_rules";
    public const string AgentToolSetRule = "/v1/agent/tools/orbit_set_rule";
    public const string AgentToolListMemory = "/v1/agent/tools/orbit_list_memory";
    public const string AgentToolReportBriefing = "/v1/agent/tools/orbit_report_briefing";
    public const string AgentToolLinkEmailThread = "/v1/agent/tools/orbit_link_email_thread";
    public const string AgentToolListTaskEmails = "/v1/agent/tools/orbit_list_task_emails";
    public const string AgentToolOpenEmail = "/v1/agent/tools/orbit_open_email";

    public const string AgentToolGetChanges = "/v1/agent/tools/orbit_get_changes";
    public const string AgentToolGetPulseDelta = "/v1/agent/tools/orbit_get_pulse_delta";
    public const string AgentToolListBlockedTasks = "/v1/agent/tools/orbit_list_blocked_tasks";
    public const string AgentToolGetAgentSnapshot = "/v1/agent/tools/orbit_get_agent_snapshot";
    public const string AgentToolHealth = "/v1/agent/tools/orbit_health";

    public const string AgentToolAddCustomField = "/v1/agent/tools/orbit_add_custom_field";
    public const string AgentToolSetCustomFieldValue = "/v1/agent/tools/orbit_set_custom_field_value";
    public const string AgentToolUpdateCustomFieldLabel = "/v1/agent/tools/orbit_update_custom_field_label";
    public const string AgentToolSaveLayout = "/v1/agent/tools/orbit_save_layout";
    public const string AgentToolApplyLayout = "/v1/agent/tools/orbit_apply_layout";
    public const string AgentToolRevertLayout = "/v1/agent/tools/orbit_revert_layout";
    public const string AgentToolDevCreateBranch = "/v1/agent/tools/orbit_dev_create_branch";
    public const string AgentToolDevWriteFile = "/v1/agent/tools/orbit_dev_write_file";
    public const string AgentToolDevBuild = "/v1/agent/tools/orbit_dev_build";

    public const string AgentSnapshot = "/v1/agent/snapshot";

    /// <summary>Paths that skip bearer auth when a Core API key is configured (liveness only).</summary>
    public static bool IsAnonymousPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && string.Equals(path.TrimEnd('/'), Health, StringComparison.OrdinalIgnoreCase);
}
