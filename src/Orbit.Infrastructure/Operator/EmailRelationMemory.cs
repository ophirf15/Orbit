using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Infrastructure.Operator;

/// <summary>
/// Lightweight Accept/Reject → operator_memory so Hermes can learn email↔task relationships later.
/// </summary>
public static class EmailRelationMemory
{
    public const string Kind = OperatorMemoryKinds.Process;

    public static void RememberDecision(OperatorMemoryStore memory, AgentSuggestionRecord suggestion, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(suggestion);

        if (!IsRelationSuggestion(suggestion.SuggestionType))
        {
            return;
        }

        var text = BuildFactText(suggestion, accepted);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var scope = string.IsNullOrWhiteSpace(suggestion.ProjectId) ? "global" : suggestion.ProjectId!;
        memory.Remember(new RememberRequest
        {
            Text = text,
            Kind = Kind,
            Scope = scope,
            Confidence = suggestion.Confidence,
            Source = accepted ? "suggestion.accepted" : "suggestion.rejected",
            EvidenceRefsJson = JsonSerializer.Serialize(new
            {
                suggestionId = suggestion.Id,
                suggestionType = suggestion.SuggestionType,
                taskId = suggestion.TaskId,
                accepted,
            }),
        });
    }

    public static IReadOnlyList<string> ListRecentFactLines(OperatorMemoryStore memory, int limit = 12)
    {
        return memory.List(limit: Math.Clamp(limit * 3, 12, 80))
            .Where(m => string.Equals(m.Kind, Kind, StringComparison.Ordinal)
                        && (m.Source?.StartsWith("suggestion.", StringComparison.Ordinal) ?? false))
            .Take(limit)
            .Select(m => m.Text)
            .ToList();
    }

    private static bool IsRelationSuggestion(string suggestionType) =>
        suggestionType is SuggestionTypes.MergeIntoTask
            or SuggestionTypes.DisambiguateEmailClaim
            or SuggestionTypes.LinkTasks;

    private static string? BuildFactText(AgentSuggestionRecord suggestion, bool accepted)
    {
        var verb = accepted ? "related" : "NOT related";
        if (string.Equals(suggestion.SuggestionType, SuggestionTypes.MergeIntoTask, StringComparison.Ordinal))
        {
            var taskHint = suggestion.TaskId ?? "task";
            return Truncate($"email-relation: mail {verb} to task {taskHint} — {suggestion.Summary}", 400);
        }

        if (string.Equals(suggestion.SuggestionType, SuggestionTypes.DisambiguateEmailClaim, StringComparison.Ordinal))
        {
            var project = suggestion.ProjectId ?? "project";
            return Truncate(
                accepted
                    ? $"email-relation: ambiguous mail assigned to project {project} — {suggestion.Summary}"
                    : $"email-relation: rejected project guess for — {suggestion.Summary}",
                400);
        }

        if (string.Equals(suggestion.SuggestionType, SuggestionTypes.LinkTasks, StringComparison.Ordinal))
        {
            return Truncate($"email-relation: task link {verb} — {suggestion.Summary}", 400);
        }

        return null;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
