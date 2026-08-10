namespace Orbit.Core.Data;

public static class OrbitDbPaths
{
    public const string DatabaseFileName = "orbit.db";

    public static string GetDatabasePath(string localDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataRoot);
        return Path.Combine(localDataRoot, DatabaseFileName);
    }
}

public static class EntityTypes
{
    public const string Project = "project";
    public const string Workstream = "workstream";
    public const string Task = "task";
    public const string Note = "note";
    public const string Blocker = "blocker";
    public const string Person = "person";
    public const string Organization = "organization";
    public const string EmailArtifact = "email_artifact";
    public const string FileArtifact = "file_artifact";
    public const string CalendarEvent = "calendar_event";
    public const string Conversation = "conversation";
    public const string AgentSuggestion = "agent_suggestion";
    public const string GeneratedArtifact = "generated_artifact";
}

public static class TaskStatuses
{
    public const string NotStarted = "not_started";
    public const string Active = "active";
    public const string Waiting = "waiting";
    public const string Blocked = "blocked";
    public const string Complete = "complete";
    public const string Archived = "archived";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NotStarted, Active, Waiting, Blocked, Complete, Archived,
    };
}

public static class RelationshipTypes
{
    public const string Serves = "serves";
    public const string MemberOf = "member_of";
    public const string LinkedToProject = "linked_to_project";
    public const string InvolvedIn = "involved_in";
}

/// <summary>
/// Directional task-to-task edges: predecessor → successor.
/// </summary>
public static class TaskDependencyTypes
{
    /// <summary>Predecessor must be complete before the successor can proceed.</summary>
    public const string Blocks = "blocks";

    /// <summary>Predecessor produces information the successor needs, but does not hard-block it.</summary>
    public const string Informs = "informs";

    /// <summary>Related work with no ordering constraint.</summary>
    public const string Relates = "relates";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Blocks, Informs, Relates,
    };

    /// <summary>Types where the successor is waiting on something from the predecessor.</summary>
    public static bool IsGating(string dependencyType) =>
        string.Equals(dependencyType, Blocks, StringComparison.Ordinal)
        || string.Equals(dependencyType, Informs, StringComparison.Ordinal);
}

public static class CreatedByActors
{
    public const string User = "user";
    public const string Agent = "agent";
    public const string System = "system";
    public const string Hermes = "hermes";
}

public static class SuggestionStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Expired = "expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Pending, Accepted, Rejected, Expired,
    };
}

public static class SuggestionTypes
{
    public const string AssignToProject = "assign_to_project";
    public const string ReviewLimbo = "review_limbo";
    public const string LinkContact = "link_contact";
    public const string ContactMerge = "contact_merge";
    /// <summary>Email claim without a clear project name/code — never silent-assign.</summary>
    public const string DisambiguateEmailClaim = "disambiguate_email_claim";
    /// <summary>Legacy demo seed type; treated as <see cref="AssignToProject"/> on accept.</summary>
    public const string AssignProjectLegacy = "assign_project";

    /// <summary>Two tasks look contingent on each other — propose a dependency edge.</summary>
    public const string LinkTasks = "link_tasks";

    /// <summary>Inbound info (email/note) appears to answer what a task is waiting on.</summary>
    public const string MergeIntoTask = "merge_into_task";

    /// <summary>A gating predecessor is satisfied — confirm the successor can proceed.</summary>
    public const string DependencyReady = "dependency_ready";

    /// <summary>Two people in the same org look like a reporting edge — confirm before writing.</summary>
    public const string ReportingRelationship = "reporting_relationship";
}
