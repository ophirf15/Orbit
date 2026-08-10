namespace Orbit.Infrastructure.Suggestions;

public sealed class AgentSuggestionRecord
{
    public required string Id { get; init; }

    public required string SuggestionType { get; init; }

    public required string Summary { get; init; }

    public string? PayloadJson { get; init; }

    public string? ProjectId { get; init; }

    public string? WorkstreamId { get; init; }

    public string? TaskId { get; init; }

    public string? NoteId { get; init; }

    public required string Status { get; init; }

    public double? Confidence { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class CreateSuggestionRequest
{
    public required string SuggestionType { get; init; }

    public required string Summary { get; init; }

    public string? PayloadJson { get; init; }

    public string? ProjectId { get; init; }

    public string? WorkstreamId { get; init; }

    public string? TaskId { get; init; }

    public string? NoteId { get; init; }

    public double? Confidence { get; init; }
}

public sealed class SuggestionAcceptResult
{
    public required AgentSuggestionRecord Suggestion { get; init; }

    public string? AppliedNoteId { get; init; }

    public string? AppliedProjectId { get; init; }

    public string? CreatedTaskId { get; init; }

    /// <summary>Task that was mutated by the accept (merge_into_task, dependency_ready).</summary>
    public string? AppliedTaskId { get; init; }

    /// <summary>Dependency edge created by accepting a link_tasks suggestion.</summary>
    public string? CreatedDependencyId { get; init; }
}
