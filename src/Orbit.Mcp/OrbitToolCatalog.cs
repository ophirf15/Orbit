namespace Orbit.Mcp;

/// <summary>
/// Allowlisted Orbit Core agent tools exposed over MCP. Source of truth for names stays aligned with
/// <c>docs/hermes/orbit-tools.md</c> and Host <c>/v1/agent/tools/*</c>.
/// </summary>
public static class OrbitToolCatalog
{
    public const string GetRelatedContext = "orbit_get_related_context";
    public const string Search = "orbit_search";
    public const string GetProject = "orbit_get_project";
    public const string GetContact = "orbit_get_contact";
    public const string UpdateContact = "orbit_update_contact";
    public const string ListContacts = "orbit_list_contacts";
    public const string ArchiveContact = "orbit_archive_contact";
    public const string FlagResident = "orbit_flag_resident";
    public const string GetWorkbench = "orbit_get_workbench";
    public const string GetCalendarContext = "orbit_get_calendar_context";
    public const string CreateProject = "orbit_create_project";
    public const string CreateWorkstream = "orbit_create_workstream";
    public const string ListWorkstreams = "orbit_list_workstreams";
    public const string CreateTask = "orbit_create_task";
    public const string UpdateTask = "orbit_update_task";
    public const string UpdateProject = "orbit_update_project";
    public const string CreateNote = "orbit_create_note";
    public const string ArchiveEntity = "orbit_archive_entity";
    public const string LinkTasks = "orbit_link_tasks";
    public const string UnlinkTasks = "orbit_unlink_tasks";
    public const string GetTaskDependencies = "orbit_get_task_dependencies";
    public const string SuggestTaskLinks = "orbit_suggest_task_links";
    public const string AcceptSuggestion = "orbit_accept_suggestion";
    public const string RejectSuggestion = "orbit_reject_suggestion";
    public const string Remember = "orbit_remember";
    public const string Forget = "orbit_forget";
    public const string ListRules = "orbit_list_rules";
    public const string SetRule = "orbit_set_rule";
    public const string ListMemory = "orbit_list_memory";
    public const string ReportBriefing = "orbit_report_briefing";
    public const string LinkEmailThread = "orbit_link_email_thread";
    public const string ListTaskEmails = "orbit_list_task_emails";
    public const string OpenEmail = "orbit_open_email";
    public const string GetChanges = "orbit_get_changes";
    public const string GetPulseDelta = "orbit_get_pulse_delta";
    public const string ListBlockedTasks = "orbit_list_blocked_tasks";
    public const string GetAgentSnapshot = "orbit_get_agent_snapshot";
    public const string Health = "orbit_health";

    public static IReadOnlyList<string> All { get; } =
    [
        GetRelatedContext,
        Search,
        GetProject,
        GetContact,
        UpdateContact,
        ListContacts,
        ArchiveContact,
        FlagResident,
        GetWorkbench,
        GetCalendarContext,
        CreateProject,
        CreateWorkstream,
        ListWorkstreams,
        CreateTask,
        UpdateTask,
        UpdateProject,
        CreateNote,
        ArchiveEntity,
        LinkTasks,
        UnlinkTasks,
        GetTaskDependencies,
        SuggestTaskLinks,
        AcceptSuggestion,
        RejectSuggestion,
        Remember,
        Forget,
        ListRules,
        SetRule,
        ListMemory,
        ReportBriefing,
        LinkEmailThread,
        ListTaskEmails,
        OpenEmail,
        GetChanges,
        GetPulseDelta,
        ListBlockedTasks,
        GetAgentSnapshot,
        Health,
    ];

    public static string Route(string toolName) => $"/v1/agent/tools/{toolName}";
}
