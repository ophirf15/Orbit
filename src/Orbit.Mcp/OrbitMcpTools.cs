using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Orbit.Mcp;

/// <summary>MCP tool surface that forwards to Orbit Core Host authenticated HTTP routes.</summary>
[McpServerToolType]
public sealed class OrbitMcpTools(OrbitCoreClient core)
{
    [McpServerTool(Name = OrbitToolCatalog.GetRelatedContext), Description(
        "Bounded Orbit context bundle for a project, workstream, or task (project-scoped extractions + linked meetings).")]
    public Task<string> GetRelatedContext(
        [Description("Target entity type: project | workstream | task")] string targetType,
        [Description("Target entity GUID")] string targetId,
        [Description("Optional attention/focus project GUID")] string? attentionProjectId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.GetRelatedContext,
            new
            {
                targetType,
                targetId,
                attentionProjectId,
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.Search), Description(
        "Global Orbit FTS search across graph, files, emails, calendar, and conversations.")]
    public Task<string> Search(
        [Description("Search query (also accepted by Core as q)")] string query,
        [Description("Optional focus project GUID")] string? focusProjectId = null,
        [Description("Optional focus meeting GUID")] string? focusMeetingId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.Search,
            new
            {
                q = query,
                query,
                focusProjectId,
                focusMeetingId,
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetProject), Description(
        "Fetch an Orbit project row plus its context bundle.")]
    public Task<string> GetProject(
        [Description("Project GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetProject, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetWorkbench), Description(
        "Fetch the Orbit workbench snapshot (project cells, accents, open task lines). Omit projectId for the root board.")]
    public Task<string> GetWorkbench(
        [Description("Optional project GUID to open that project's board")] string? projectId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetWorkbench, new { projectId }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetCalendarContext), Description(
        "Typed upcoming calendar context: next meetings, organizer, linked entities. Supports changedSince for incremental polling.")]
    public Task<string> GetCalendarContext(
        [Description("Lookahead window in days (default 14, max 90)")] int? days = null,
        [Description("Max meetings to return (default 40, max 100)")] int? limit = null,
        [Description("ISO-8601 timestamp; only return meetings updated at or after this time")] DateTimeOffset? changedSince = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetCalendarContext, new { days, limit, changedSince }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetContact), Description(
        "Fetch Orbit contact detail JSON by id.")]
    public Task<string> GetContact(
        [Description("Contact GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetContact, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListContacts), Description(
        "List Orbit contacts. Filter by category (company|client|vendor) or disposition (flagged_resident). "
        + "Default excludes archived and excluded_resident.")]
    public Task<string> ListContacts(
        [Description("Optional category: company | client | vendor (omit for all; use pending via Core list API)")] string? category = null,
        [Description("Optional disposition: flagged_resident")] string? disposition = null,
        [Description("Max rows (default 100, max 500)")] int? limit = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ListContacts,
            new { category, disposition, limit },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.UpdateContact), Description(
        "Patch an Orbit contact. Prefer patch JSON; you may also pass mobile/phone/title/category flat. "
        + "When a signature shows a phone number, always include mobile or phone.")]
    public Task<string> UpdateContact(
        [Description("Contact GUID")] string id,
        [Description("JSON object of patch fields")] JsonElement? patch = null,
        [Description("Optional fact provenance note")] string? provenance = null,
        [Description("Optional actor label")] string? actor = null,
        [Description("Mobile phone (also accepted inside patch.mobile)")] string? mobile = null,
        [Description("Desk/direct phone (also accepted inside patch.phone)")] string? phone = null,
        [Description("Job title")] string? title = null,
        [Description("Organization display name")] string? organizationName = null,
        [Description("Person category: company|client|vendor")] string? category = null,
        [Description("Display name")] string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        var merged = MergeContactPatch(patch, mobile, phone, title, organizationName, category, displayName);
        return core.CallToolAsync(
            OrbitToolCatalog.UpdateContact,
            new { id, patch = merged, provenance, actor },
            cancellationToken);
    }

    private static Dictionary<string, object?> MergeContactPatch(
        JsonElement? patch,
        string? mobile,
        string? phone,
        string? title,
        string? organizationName,
        string? category,
        string? displayName)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (patch is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                map[prop.Name] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => prop.Value.GetRawText(),
                };
            }
        }

        void Set(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[key] = value.Trim();
            }
        }

        Set("mobile", mobile);
        Set("phone", phone);
        Set("title", title);
        Set("organizationName", organizationName);
        Set("category", category);
        Set("displayName", displayName);
        return map;
    }

    [McpServerTool(Name = OrbitToolCatalog.ArchiveContact), Description(
        "Soft-archive a contact. Set excludeAsResident=true to mark excluded_resident (do not track).")]
    public Task<string> ArchiveContact(
        [Description("Contact GUID")] string id,
        [Description("If true, set disposition to excluded_resident")] bool excludeAsResident = false,
        [Description("Optional provenance note")] string? provenance = null,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ArchiveContact,
            new { id, excludeAsResident, provenance, actor },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.FlagResident), Description(
        "Shorthand: set contact disposition to flagged_resident for People Review queue (do not keep as tracked category).")]
    public Task<string> FlagResident(
        [Description("Contact GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.FlagResident, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.CreateProject), Description(
        "Create a new Orbit project and add it to the Pulse orbit. "
        + "Search/attach first: if a similar name or alias already exists, the tool returns HTTP 409 with candidates — do not force-create duplicates. "
        + "Pass force=true only after the operator confirms create anyway. "
        + "Hierarchy: project → workstreams (sub-areas) → tasks.")]
    public Task<string> CreateProject(
        [Description("Project name")] string name,
        [Description("Optional short summary")] string? summary = null,
        [Description("Optional short code")] string? code = null,
        [Description("Optional aliases to add on create")] string[]? aliases = null,
        [Description("Add to Pulse orbit (default true)")] bool inOrbit = true,
        [Description("Create even when near-duplicate candidates exist (operator override)")] bool force = false,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.CreateProject,
            new { name, summary, code, aliases, inOrbit, force },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.CreateWorkstream), Description(
        "Create a workstream (sub-area) under a project — e.g. FF&E, Internet, Leasing under a property project.")]
    public Task<string> CreateWorkstream(
        [Description("Parent project GUID")] string projectId,
        [Description("Workstream / sub-area name")] string name,
        [Description("Optional next action for the workstream")] string? nextAction = null,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.CreateWorkstream,
            new { projectId, name, nextAction, actor },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListWorkstreams), Description(
        "List workstreams (sub-areas) for a project.")]
    public Task<string> ListWorkstreams(
        [Description("Project GUID")] string projectId,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ListWorkstreams,
            new { projectId },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.CreateTask), Description(
        "Create an Orbit-owned task (audited mutation). Prefer over inventing SQL. "
        + "Always set nextAction; set body as the living brief when known. "
        + "Pass workstreamId to nest under a project sub-area.")]
    public Task<string> CreateTask(
        [Description("Task title")] string title,
        [Description("Project GUID")] string projectId,
        [Description("Optional workstream GUID (sub-area under the project)")] string? workstreamId = null,
        [Description("Optional status enum string")] string? status = null,
        [Description("Optional next action text")] string? nextAction = null,
        [Description("Optional living brief / body markdown")] string? body = null,
        [Description("Optional actor label (e.g. hermes, telegram)")] string? actor = null,
        [Description("Optional provenance JSON object: actor, channel, hermesSessionId, externalUserId")] string? provenanceJson = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.CreateTask,
            new
            {
                title,
                projectId,
                workstreamId,
                status,
                nextAction,
                body,
                actor,
                provenance = ParseOptionalJson(provenanceJson),
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.UpdateTask), Description(
        "Update an Orbit task (audited). Supports move via projectId (+ optional workstreamId). "
        + "priority: 1=important 0=less. urgency: 1=urgent 0=less. clearWorkstream=true drops workstream.")]
    public Task<string> UpdateTask(
        [Description("Task GUID")] string id,
        [Description("Optional new title")] string? title = null,
        [Description("Optional status enum string")] string? status = null,
        [Description("Optional next action text")] string? nextAction = null,
        [Description("Optional living brief / body markdown")] string? body = null,
        [Description("Optional due date (YYYY-MM-DD or ISO)")] string? dueAt = null,
        [Description("Optional importance: 1=important, 0=less important")] int? priority = null,
        [Description("Optional urgency override: 1=urgent, 0=less urgent")] int? urgency = null,
        [Description("Optional destination project GUID (moves the task)")] string? projectId = null,
        [Description("Optional workstream GUID on the (new) project")] string? workstreamId = null,
        [Description("When true, clear workstream assignment")] bool clearWorkstream = false,
        [Description("Optional actor label")] string? actor = null,
        [Description("Optional provenance JSON object")] string? provenanceJson = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.UpdateTask,
            new
            {
                id,
                title,
                status,
                nextAction,
                body,
                dueAt,
                priority,
                urgency,
                projectId,
                workstreamId,
                clearWorkstream,
                actor,
                provenance = ParseOptionalJson(provenanceJson),
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.UpdateProject), Description(
        "Update an Orbit project name, summary, code, aliases, dossier fields, and/or workbench stripe color. "
        + "dossier is structured operator context: address, ownerClient, phase, portfolio, linkedFolder, "
        + "criticalContacts[{name,role,personId,contact}], mailboxSources[], calendarSources[], currentPriorities[]. "
        + "addAliases/removeAliases are operator nicknames used for email matching. "
        + "accentColor accepts #RRGGBB or preset names: blue, sky, teal, green, amber, rose, violet, slate; "
        + "use default/none/clear to restore the theme stripe.")]
    public Task<string> UpdateProject(
        [Description("Project GUID")] string id,
        [Description("Optional new project name")] string? name = null,
        [Description("Optional project summary")] string? summary = null,
        [Description("Optional short code")] string? code = null,
        [Description("When true, clear the project code")] bool clearCode = false,
        [Description("Aliases to add")] string[]? addAliases = null,
        [Description("Aliases (or alias ids) to remove")] string[]? removeAliases = null,
        [Description("Optional accent: #RRGGBB or preset name (blue, teal, …)")] string? accentColor = null,
        [Description("Optional dossier patch object")] object? dossier = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.UpdateProject,
            new { id, name, summary, code, clearCode, addAliases, removeAliases, accentColor, dossier },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.MergeProject), Description(
        "Operator-initiated merge of one project into another. Moves tasks/notes/links/aliases, "
        + "archives the source, writes audit events. Call with previewOnly=true first to see counts. "
        + "Pass force=true only after the operator confirms past warnings (e.g. dual home folders). "
        + "Never invent personal site names — use project GUIDs the operator chose.")]
    public Task<string> MergeProject(
        [Description("Source project GUID (will be archived)")] string sourceProjectId,
        [Description("Target project GUID (receives moved rows)")] string targetProjectId,
        [Description("When true, return preview counts only")] bool previewOnly = false,
        [Description("Proceed despite warnings")] bool force = false,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.MergeProject,
            new { sourceProjectId, targetProjectId, previewOnly, force, actor },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.AddProjectAlias), Description(
        "Add an operator-defined alias/nickname for a project (used for email and create-project matching).")]
    public Task<string> AddProjectAlias(
        [Description("Project GUID")] string projectId,
        [Description("Alias text")] string alias,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.AddProjectAlias,
            new { projectId, alias },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.RemoveProjectAlias), Description(
        "Remove a project alias by text or alias id.")]
    public Task<string> RemoveProjectAlias(
        [Description("Project GUID")] string projectId,
        [Description("Alias text")] string? alias = null,
        [Description("Alias GUID")] string? aliasId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.RemoveProjectAlias,
            new { projectId, alias, aliasId },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListProjectAliases), Description(
        "List operator-defined aliases for a project.")]
    public Task<string> ListProjectAliases(
        [Description("Project GUID")] string projectId,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ListProjectAliases,
            new { projectId },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.CreateNote), Description(
        "Create an Orbit capture note. Omit projectId to land in Limbo.")]
    public Task<string> CreateNote(
        [Description("Note text")] string text,
        [Description("Optional project GUID")] string? projectId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.CreateNote,
            new { text, projectId },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ArchiveEntity), Description(
        "Soft-archive an Orbit entity (audited). Nothing is hard-deleted.")]
    public Task<string> ArchiveEntity(
        [Description("Entity type: project | workstream | task | note | blocker")] string entityType,
        [Description("Entity GUID")] string entityId,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ArchiveEntity,
            new { entityType, entityId, actor },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.LinkTasks), Description(
        "Record that one task depends on another (predecessor → successor). Use when two tasks are "
        + "contingent: 'blocks' when the successor cannot start until the predecessor completes, "
        + "'informs' when the predecessor produces information the successor needs, 'relates' for "
        + "association only. Set expects to the specific thing being waited on so Orbit can watch for it.")]
    public Task<string> LinkTasks(
        [Description("Task GUID that must happen first / supplies the info")] string predecessorTaskId,
        [Description("Task GUID that is waiting")] string successorTaskId,
        [Description("Dependency type: blocks | informs | relates (default blocks)")] string? dependencyType = null,
        [Description("What the waiting task needs, e.g. 'number of phone lines'")] string? expects = null,
        [Description("Why these are linked")] string? reason = null,
        [Description("Confidence 0-1 when inferred rather than user-stated")] double? confidence = null,
        [Description("Optional evidence reference, e.g. an email GUID")] string? evidenceRef = null,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.LinkTasks,
            new
            {
                predecessorTaskId,
                successorTaskId,
                dependencyType,
                expects,
                reason,
                confidence,
                evidenceRef,
                actor,
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.UnlinkTasks), Description(
        "Remove a task dependency edge by its dependency id. The tasks themselves are untouched.")]
    public Task<string> UnlinkTasks(
        [Description("Dependency GUID from orbit_get_task_dependencies")] string dependencyId,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.UnlinkTasks,
            new { dependencyId, actor },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetTaskDependencies), Description(
        "List what a task is waiting on and what is waiting on it, including whether each upstream "
        + "task is already satisfied. Call this before answering whether a task can proceed.")]
    public Task<string> GetTaskDependencies(
        [Description("Task GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetTaskDependencies, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.SuggestTaskLinks), Description(
        "Run Orbit's relationship heuristics for a task and return any pending link proposals. "
        + "Proposals require user confirmation; this does not create links.")]
    public Task<string> SuggestTaskLinks(
        [Description("Task GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.SuggestTaskLinks, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.AcceptSuggestion), Description(
        "Accept a pending Orbit suggestion, applying its mutation (assign to project, link tasks, "
        + "or merge inbound info into a task). Only call after the user has confirmed.")]
    public Task<string> AcceptSuggestion(
        [Description("Suggestion GUID")] string id,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.AcceptSuggestion, new { id, actor }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.RejectSuggestion), Description(
        "Reject a pending Orbit suggestion so it stops being offered.")]
    public Task<string> RejectSuggestion(
        [Description("Suggestion GUID")] string id,
        [Description("Optional actor label")] string? actor = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.RejectSuggestion, new { id, actor }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.Remember), Description(
        "Store a curated operator memory fact (preference, working_style, project_fact, person_fact, process).")]
    public Task<string> Remember(
        [Description("Fact text")] string text,
        [Description("Kind: preference | working_style | project_fact | person_fact | process")] string kind,
        [Description("Scope: global or project id")] string? scope = null,
        [Description("Optional source label")] string? source = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.Remember,
            new { text, kind, scope = scope ?? "global", source },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.Forget), Description("Archive an operator memory fact by id.")]
    public Task<string> Forget(
        [Description("Memory fact GUID")] string id,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.Forget, new { id }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListRules), Description("List enabled Orbit standing rules for the duty operator.")]
    public Task<string> ListRules(CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.ListRules, new { }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.SetRule), Description(
        "Create a standing rule that can auto-apply big moves (create_task, update_task, set_blocker, link_email_thread, create_note).")]
    public Task<string> SetRule(
        [Description("Rule display name")] string name,
        [Description("Trigger kind e.g. email.ingested")] string triggerKind,
        [Description("Action kind e.g. create_task")] string actionKind,
        [Description("Optional match JSON")] string? matchJson = null,
        [Description("Optional params JSON")] string? paramsJson = null,
        [Description("Whether confirm is still required")] bool requireConfirm = false,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.SetRule,
            new
            {
                name,
                triggerKind,
                actionKind,
                matchJson,
                paramsJson,
                enabled = true,
                requireConfirm,
            },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListMemory), Description("List curated operator memory facts.")]
    public Task<string> ListMemory(
        [Description("Optional scope filter")] string? scope = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.ListMemory, new { scope }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ReportBriefing), Description(
        "Post a Work Jarvis duty/pulse briefing into Orbit so Pulse and the Hermes strip show it. "
        + "Call at the end of duty-scan, pulse-refresh, chase-waiting, or email triage when you have "
        + "something material to say. Pass briefing='[SILENT]' to no-op.")]
    public Task<string> ReportBriefing(
        [Description("Short ranked briefing text for the operator")] string briefing,
        [Description("Trigger kind e.g. duty.scan | pulse.refresh | chase.waiting | email.ingested")] string? triggerKind = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.ReportBriefing,
            new { briefing, triggerKind = triggerKind ?? "duty.scan" },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.LinkEmailThread), Description(
        "Link an Outlook conversation to a task for durable tracking.")]
    public Task<string> LinkEmailThread(
        [Description("Task GUID")] string taskId,
        [Description("Outlook conversation id")] string conversationId,
        [Description("Optional anchor email artifact id")] string? anchorEmailId = null,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(
            OrbitToolCatalog.LinkEmailThread,
            new { taskId, conversationId, anchorEmailId, actor = "hermes" },
            cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListTaskEmails), Description("List email threads linked to a task.")]
    public Task<string> ListTaskEmails(
        [Description("Task GUID")] string taskId,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.ListTaskEmails, new { taskId }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.OpenEmail), Description(
        "Resolve the on-disk .msg path for an ingested email (Orbit UI / shell can open it).")]
    public Task<string> OpenEmail(
        [Description("Email artifact GUID")] string emailId,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.OpenEmail, new { emailId }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetChanges), Description(
        "List Orbit change-log events since a cursor (Hermes monitor fuel).")]
    public Task<string> GetChanges(
        [Description("Exclusive revision cursor (0 = from start)")] long cursor = 0,
        [Description("Max events")] int limit = 200,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetChanges, new { cursor, limit }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetPulseDelta), Description(
        "Pulse/concern delta since a change cursor.")]
    public Task<string> GetPulseDelta(
        [Description("Exclusive revision cursor")] long cursor = 0,
        [Description("Max events")] int limit = 200,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetPulseDelta, new { cursor, limit }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.ListBlockedTasks), Description(
        "List blocked Orbit tasks (optional project filter).")]
    public Task<string> ListBlockedTasks(
        [Description("Optional project GUID")] string? projectId = null,
        [Description("Max rows")] int limit = 100,
        CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.ListBlockedTasks, new { projectId, limit }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.GetAgentSnapshot), Description(
        "Stable Orbit agent snapshot for monitor_script hashing (no volatile timestamps).")]
    public Task<string> GetAgentSnapshot(CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.GetAgentSnapshot, new { }, cancellationToken);

    [McpServerTool(Name = OrbitToolCatalog.Health), Description(
        "Orbit Core readiness for Work Jarvis: change cursor, Hermes URL/key, webhook secret present.")]
    public Task<string> Health(CancellationToken cancellationToken = default)
        => core.CallToolAsync(OrbitToolCatalog.Health, new { }, cancellationToken);

    private static object? ParseOptionalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
