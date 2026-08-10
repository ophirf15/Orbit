namespace Orbit.Infrastructure.Context;

public static class ContextTargetTypes
{
    public const string Project = "project";
    public const string Workstream = "workstream";
    public const string Task = "task";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Project, Workstream, Task,
    };
}

public sealed class ContextBundle
{
    public required string TargetType { get; init; }

    public required string TargetId { get; init; }

    public required string ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public string? ProjectSummary { get; init; }

    public string? WorkstreamId { get; init; }

    public string? TaskId { get; init; }

    public string? AttentionProjectId { get; init; }

    public bool AttentionAligned { get; init; }

    /// <summary>Primary project home folder on disk, when set.</summary>
    public string? HomeFolderPath { get; init; }

    /// <summary>Writable <c>.orbit</c> sandbox under the home folder.</summary>
    public string? OrbitSandboxPath { get; init; }

    /// <summary>Reminder for agents: only <c>.orbit</c> is writable under home.</summary>
    public string FileWritePolicy { get; init; } =
        "Project home is read-only except the .orbit sandbox under that home.";

    public required IReadOnlyList<ContextBundleTask> Tasks { get; init; }

    public required IReadOnlyList<ContextBundleBlocker> Blockers { get; init; }

    public required IReadOnlyList<ContextBundleNote> Notes { get; init; }

    public required IReadOnlyList<ContextBundleEmail> Emails { get; init; }

    public required IReadOnlyList<ContextBundleContact> Contacts { get; init; }

    public required IReadOnlyList<ContextBundleFile> Files { get; init; }

    public required IReadOnlyList<ContextBundleMeeting> Meetings { get; init; }

    public required IReadOnlyList<ContextBundleSuggestion> Suggestions { get; init; }

    public required IReadOnlyList<ContextBundleRelatedEntity> RelatedEntities { get; init; }
}

public sealed class ContextBundleTask
{
    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public string? NextAction { get; init; }

    public string? WorkstreamId { get; init; }
}

public sealed class ContextBundleBlocker
{
    public required string Id { get; init; }

    public required string Summary { get; init; }

    public required string Status { get; init; }

    public string? TaskId { get; init; }
}

public sealed class ContextBundleNote
{
    public required string Id { get; init; }

    public required string OriginalText { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ContextBundleEmail
{
    public required string Id { get; init; }

    public string? Subject { get; init; }

    public string? SentAt { get; init; }

    public string? BodyPreview { get; init; }

    public required IReadOnlyList<ContextBundleExtraction> Extractions { get; init; }
}

public sealed class ContextBundleExtraction
{
    public required string Id { get; init; }

    public required string ExtractionType { get; init; }

    public required string Summary { get; init; }

    public required string ProjectId { get; init; }

    public string? WorkstreamId { get; init; }

    public double? Confidence { get; init; }
}

public sealed class ContextBundleContact
{
    public required string PersonId { get; init; }

    public required string DisplayName { get; init; }
}

public sealed class ContextBundleFile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Path { get; init; }

    public string? Extension { get; init; }
}

public sealed class ContextBundleMeeting
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? StartsAt { get; init; }

    public string? EndsAt { get; init; }

    public string? Location { get; init; }

    public double? AttentionScore { get; init; }

    public string? SourceName { get; init; }

    public string? MailboxName { get; init; }

    public string? CalendarName { get; init; }
}

public sealed class ContextBundleSuggestion
{
    public required string Id { get; init; }

    public required string Summary { get; init; }

    public required string Status { get; init; }

    public string? SuggestionType { get; init; }

    public string? NoteId { get; init; }

    public double? Confidence { get; init; }
}

public sealed class ContextBundleRelatedEntity
{
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Label { get; init; }

    public string? RelationshipType { get; init; }
}
