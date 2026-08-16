using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Infrastructure.Operator;

/// <summary>
/// Accept / Reject / Always → <c>operator_memory</c> so Hermes learns from operator decisions
/// and improves future recommendations (email relations, assign, links, limbo, etc.).
/// </summary>
public static class EmailRelationMemory
{
    public const string Kind = OperatorMemoryKinds.Process;

    public static void RememberDecision(OperatorMemoryStore memory, AgentSuggestionRecord suggestion, bool accepted)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(suggestion);

        WriteFact(
            memory,
            suggestion,
            accepted,
            always: false,
            source: accepted ? "suggestion.accepted" : "suggestion.rejected");
    }

    /// <summary>
    /// Operator chose Accept + Always — record the standing preference as an extra training signal
    /// (Accept already wrote the accepted fact).
    /// </summary>
    public static void RememberAlways(OperatorMemoryStore memory, AgentSuggestionRecord suggestion)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(suggestion);

        WriteFact(
            memory,
            suggestion,
            accepted: true,
            always: true,
            source: "suggestion.always");
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

    private static void WriteFact(
        OperatorMemoryStore memory,
        AgentSuggestionRecord suggestion,
        bool accepted,
        bool always,
        string source)
    {
        var text = BuildFactText(suggestion, accepted, always);
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
            Source = source,
            EvidenceRefsJson = JsonSerializer.Serialize(new
            {
                suggestionId = suggestion.Id,
                suggestionType = suggestion.SuggestionType,
                taskId = suggestion.TaskId,
                projectId = suggestion.ProjectId,
                accepted,
                always,
                confidence = suggestion.Confidence,
            }),
        });
    }

    private static string? BuildFactText(AgentSuggestionRecord suggestion, bool accepted, bool always)
    {
        var type = suggestion.SuggestionType ?? string.Empty;
        var summary = Truncate(suggestion.Summary ?? string.Empty, 220);
        var conf = suggestion.Confidence is null
            ? "n/a"
            : suggestion.Confidence.Value.ToString("0.00");

        if (always)
        {
            return Truncate(
                $"suggestion-train: ALWAYS apply {type} when similar — {summary} (conf {conf})",
                400);
        }

        var verb = accepted ? "ACCEPTED" : "REJECTED";

        if (string.Equals(type, SuggestionTypes.MergeIntoTask, StringComparison.Ordinal))
        {
            var taskHint = suggestion.TaskId ?? "task";
            var related = accepted ? "related" : "NOT related";
            return Truncate(
                $"suggestion-train: {verb} merge — email {related} to task {taskHint} — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.DisambiguateEmailClaim, StringComparison.Ordinal))
        {
            var project = suggestion.ProjectId ?? "project";
            return Truncate(
                accepted
                    ? $"suggestion-train: {verb} email→project {project} — {summary} (conf {conf})"
                    : $"suggestion-train: {verb} project guess — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.LinkTasks, StringComparison.Ordinal))
        {
            var related = accepted ? "related" : "NOT related";
            return Truncate(
                $"suggestion-train: {verb} task link {related} — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.AssignToProject, StringComparison.Ordinal)
            || string.Equals(type, SuggestionTypes.AssignProjectLegacy, StringComparison.Ordinal))
        {
            return Truncate(
                $"suggestion-train: {verb} assign_to_project — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.ReviewLimbo, StringComparison.Ordinal))
        {
            return Truncate(
                $"suggestion-train: {verb} review_limbo — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.DependencyReady, StringComparison.Ordinal))
        {
            return Truncate(
                $"suggestion-train: {verb} dependency_ready — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.ReportingRelationship, StringComparison.Ordinal))
        {
            return Truncate(
                $"suggestion-train: {verb} reporting_relationship — {summary} (conf {conf})",
                400);
        }

        if (string.Equals(type, SuggestionTypes.LinkContact, StringComparison.Ordinal)
            || string.Equals(type, SuggestionTypes.ContactMerge, StringComparison.Ordinal))
        {
            return Truncate(
                $"suggestion-train: {verb} {type} — {summary} (conf {conf})",
                400);
        }

        return Truncate($"suggestion-train: {verb} {type} — {summary} (conf {conf})", 400);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";
}
