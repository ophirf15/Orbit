namespace Orbit.Core.Workbench;

/// <summary>Stable kind ids for task history facts (Host + App share these strings).</summary>
public static class TaskTimelineKinds
{
    public const string Created = "created";
    public const string Status = "status";
    public const string BriefUpdate = "brief_update";
    public const string Note = "note";
    public const string EmailLinked = "email_linked";
    public const string FileLinked = "file_linked";
    public const string BlockerSet = "blocker_set";
    public const string BlockerCleared = "blocker_cleared";
    public const string WaitingOnLinked = "waiting_on_linked";
    public const string Change = "change";
}

/// <summary>Raw operational fact used by <see cref="TaskTimelineMapper"/>.</summary>
public sealed class TaskTimelineFact
{
    public required string Kind { get; init; }

    /// <summary>ISO-8601 timestamp when the fact occurred.</summary>
    public required string At { get; init; }

    /// <summary>Short subject (note excerpt, email subject, blocker summary, counterpart title).</summary>
    public string? Summary { get; init; }

    /// <summary>Optional extra detail (expects, reason, source event).</summary>
    public string? Detail { get; init; }

    /// <summary>Human status label when <see cref="Kind"/> is status (e.g. Waiting).</summary>
    public string? StatusLabel { get; init; }

    public string? SourceEvent { get; init; }

    /// <summary>Optional key to collapse duplicates across audit + change log + graph rows.</summary>
    public string? DedupeKey { get; init; }
}

/// <summary>One compact chronological line for History / Overview recent.</summary>
public sealed class TaskTimelineLine
{
    public required string Kind { get; init; }

    public required string At { get; init; }

    public required string WhenLabel { get; init; }

    public required string Text { get; init; }

    public DateTimeOffset? AtUtc { get; init; }
}
