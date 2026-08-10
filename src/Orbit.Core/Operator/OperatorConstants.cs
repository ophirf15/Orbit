namespace Orbit.Core.Operator;

public static class OperatorTriggers
{
    public const string EmailIngested = "email.ingested";
    public const string CalendarSoon = "calendar.soon";
    public const string DutyScan = "duty.scan";
    public const string PulseRefresh = "pulse.refresh";
    public const string ChaseWaiting = "chase.waiting";
    public const string TaskStalled = "task.stalled";
    public const string NoteCreated = "note.created";
    public const string TaskUpdated = "task.updated";
    public const string SuggestionAlways = "suggestion.always";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EmailIngested, CalendarSoon, DutyScan, PulseRefresh, ChaseWaiting, TaskStalled, NoteCreated, TaskUpdated, SuggestionAlways,
    };
}

public static class OperatorActions
{
    public const string CreateTask = "create_task";
    public const string UpdateTask = "update_task";
    public const string SetBlocker = "set_blocker";
    public const string LinkEmailThread = "link_email_thread";
    public const string CreateNote = "create_note";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        CreateTask, UpdateTask, SetBlocker, LinkEmailThread, CreateNote,
    };
}

public static class OperatorMemoryKinds
{
    public const string Preference = "preference";
    public const string WorkingStyle = "working_style";
    public const string ProjectFact = "project_fact";
    public const string PersonFact = "person_fact";
    public const string Process = "process";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Preference, WorkingStyle, ProjectFact, PersonFact, Process,
    };
}

public static class OperatorRunStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}
