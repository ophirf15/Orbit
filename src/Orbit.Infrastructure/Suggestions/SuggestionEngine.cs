using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Suggestions;

/// <summary>
/// Heuristic ambient suggestions (no LLM). Invoked by AgentEventWorker after debounced events.
/// </summary>
public sealed class SuggestionEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SuggestionStore _suggestions;

    public SuggestionEngine(SqliteConnectionFactory factory, SuggestionStore suggestions)
    {
        _factory = factory;
        _suggestions = suggestions;
    }

    public IReadOnlyList<AgentSuggestionRecord> ProcessNoteCreated(string noteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, original_text, project_id, is_limbo
            FROM notes
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", noteId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return [];
        }

        var text = reader.GetString(1);
        var projectId = reader.IsDBNull(2) ? null : reader.GetString(2);
        var isLimbo = reader.GetInt32(3) == 1;
        reader.Close();

        if (!isLimbo || !string.IsNullOrWhiteSpace(projectId))
        {
            return [];
        }

        var created = new List<AgentSuggestionRecord>();
        var match = FindProjectMatch(connection, text);
        if (match is null)
        {
            // Limbo stays visible on the workbench. Do not create "Review unassigned capture"
            // chores — that asks the user to approve thinking (constitution: approve merges only).
            return created;
        }

        if (!_suggestions.HasPendingForNote(noteId, SuggestionTypes.AssignToProject))
        {
            var payload = JsonSerializer.Serialize(new
            {
                action = SuggestionTypes.AssignToProject,
                noteId,
                projectId = match.Id,
                explanation = $"Note text matches project '{match.Name}'.",
                evidence = new[] { $"matched:{(match.Code is null ? "name" : "name_or_code")}:{match.Name}" },
            }, JsonOptions);

            var suggestion = _suggestions.Create(new CreateSuggestionRequest
            {
                SuggestionType = SuggestionTypes.AssignToProject,
                Summary = $"Assign to {match.Name}",
                PayloadJson = payload,
                ProjectId = match.Id,
                NoteId = noteId,
                GroupKey = SuggestionHygiene.AssignToProjectKey(noteId),
                Confidence = match.Confidence,
            });

            // High-confidence project match is an auto-link, not a merge approval.
            if (match.Confidence >= 0.85)
            {
                try
                {
                    var accepted = _suggestions.Accept(suggestion.Id, actor: "heuristic-auto");
                    created.Add(accepted.Suggestion);
                }
                catch
                {
                    created.Add(suggestion);
                }
            }
            else
            {
                created.Add(suggestion);
            }
        }

        return created;
    }

    private static ProjectMatch? FindProjectMatch(SqliteConnection connection, string noteText)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY length(name) DESC;
            """;

        var text = noteText;
        ProjectMatch? best = null;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (ContainsToken(text, name))
            {
                var confidence = name.Length >= 4 ? 0.85 : 0.7;
                if (best is null || confidence > best.Confidence)
                {
                    best = new ProjectMatch(id, name, code, confidence);
                }
            }
            else if (!string.IsNullOrWhiteSpace(code) && ContainsToken(text, code))
            {
                const double confidence = 0.8;
                if (best is null || confidence > best.Confidence)
                {
                    best = new ProjectMatch(id, name, code, confidence);
                }
            }
        }

        return best;
    }

    private static bool ContainsToken(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProjectMatch(string Id, string Name, string? Code, double Confidence);
}
