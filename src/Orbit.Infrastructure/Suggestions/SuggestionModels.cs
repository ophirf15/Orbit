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

    public string? GroupKey { get; init; }

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

    /// <summary>
    /// Stable dedupe key within <see cref="SuggestionType"/> for pending rows.
    /// When set, Create will not stack a second pending suggestion for the same type+key.
    /// </summary>
    public string? GroupKey { get; init; }

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

public sealed class SuggestionBatchDecideItemResult
{
    public required string Id { get; init; }

    public required bool Ok { get; init; }

    public string? Error { get; init; }

    public AgentSuggestionRecord? Suggestion { get; init; }

    public string? AppliedNoteId { get; init; }

    public string? AppliedProjectId { get; init; }

    public string? CreatedTaskId { get; init; }
}

/// <summary>Confidence / age thresholds for suggestion hygiene (badge vs low queue).</summary>
public static class SuggestionHygiene
{
    /// <summary>Pending suggestions below this (or null confidence) are low-queue, not badge noise.</summary>
    public const double ActionableMinConfidence = 0.55;

    public static readonly TimeSpan DefaultExpireAge = TimeSpan.FromDays(14);

    public const string QueueReview = "review";
    public const string QueueLow = "low";

    public static string MergeIntoTaskKey(string taskId, string sourceId) =>
        $"{taskId.Trim()}|{sourceId.Trim()}";

    public static string LinkTasksKey(string predecessorTaskId, string successorTaskId, string? dependencyType) =>
        $"{predecessorTaskId.Trim()}|{successorTaskId.Trim()}|{(dependencyType ?? string.Empty).Trim()}";

    public static string DisambiguateEmailKey(string emailId) => emailId.Trim();

    public static string AssignToProjectKey(string noteId) => noteId.Trim();

    public static string DependencyReadyKey(string successorTaskId, string dependencyId) =>
        $"{successorTaskId.Trim()}|{dependencyId.Trim()}";

    public static string ReportingRelationshipKey(string personId, string reportsToPersonId) =>
        $"{personId.Trim()}|{reportsToPersonId.Trim()}";

    public static string ContactMergeKey(string personIdA, string personIdB)
    {
        var a = personIdA.Trim();
        var b = personIdB.Trim();
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    public static bool IsLowConfidence(double? confidence) =>
        confidence is null || confidence.Value < ActionableMinConfidence;
}
