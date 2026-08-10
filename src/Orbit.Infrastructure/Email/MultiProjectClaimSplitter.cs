using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Heuristic multi-project claim splitter (no LLM). When an email body mentions multiple
/// known project names/codes, ensures separate <c>email_extractions</c> per project.
/// Ambiguous action language with no project name becomes an agent suggestion, not a hard assign.
/// </summary>
public sealed class MultiProjectClaimSplitter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SuggestionStore _suggestions;

    public MultiProjectClaimSplitter(SqliteConnectionFactory factory, SuggestionStore suggestions)
    {
        _factory = factory;
        _suggestions = suggestions;
    }

    public ClaimSplitResult ProcessEmail(string emailId, string? bodyText, string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);

        using var connection = _factory.CreateConnection();
        if (!EmailExists(connection, emailId))
        {
            throw new ArgumentException("Email was not found.", nameof(emailId));
        }

        var haystack = BuildHaystack(bodyText, subject);
        var mentions = FindProjectMentions(connection, haystack);
        var createdExtractions = new List<string>();
        var linkedProjects = new List<string>();

        if (mentions.Count >= 1)
        {
            foreach (var mention in mentions)
            {
                EnsureProjectLink(connection, emailId, mention.Id);
                linkedProjects.Add(mention.Id);

                var summary = BuildExtractionSummary(haystack, mention);
                var extractionId = EnsureExtraction(connection, emailId, mention, summary);
                if (extractionId is not null)
                {
                    createdExtractions.Add(extractionId);
                }
            }

            return new ClaimSplitResult
            {
                MentionedProjectIds = mentions.Select(m => m.Id).ToList(),
                CreatedExtractionIds = createdExtractions,
                LinkedProjectIds = linkedProjects.Distinct(StringComparer.Ordinal).ToList(),
                SuggestionId = null,
                WasAmbiguous = false,
            };
        }

        // No clear project name/code — do not invent a hard extraction.
        string? suggestionId = null;
        if (LooksLikeActionableClaim(haystack))
        {
            suggestionId = EnsureAmbiguousSuggestion(emailId, haystack);
        }

        return new ClaimSplitResult
        {
            MentionedProjectIds = [],
            CreatedExtractionIds = [],
            LinkedProjectIds = [],
            SuggestionId = suggestionId,
            WasAmbiguous = suggestionId is not null,
        };
    }

    private string? EnsureAmbiguousSuggestion(string emailId, string haystack)
    {
        if (HasPendingDisambiguation(emailId))
        {
            return null;
        }

        var snippet = haystack.Length <= 160 ? haystack : haystack[..160];
        var payload = JsonSerializer.Serialize(new
        {
            action = SuggestionTypes.DisambiguateEmailClaim,
            emailId,
            explanation = "Email claim has no clear project name/code; do not silently assign.",
            evidence = new[] { "no_project_mention", $"snippet:{snippet}" },
            candidates = Array.Empty<object>(),
        }, JsonOptions);

        var suggestion = _suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.DisambiguateEmailClaim,
            Summary = "Ambiguous email claim — pick a project",
            PayloadJson = payload,
            Confidence = 0.35,
        });
        return suggestion.Id;
    }

    private bool HasPendingDisambiguation(string emailId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM agent_suggestions
            WHERE suggestion_type = $type
              AND status = $pending
              AND archived_at IS NULL
              AND payload_json LIKE $needle
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$type", SuggestionTypes.DisambiguateEmailClaim);
        cmd.Parameters.AddWithValue("$pending", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$needle", "%\"emailId\":\"" + emailId + "\"%");
        return cmd.ExecuteScalar() is not null;
    }

    private static string BuildHaystack(string? bodyText, string? subject)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject))
        {
            parts.Add(subject.Trim());
        }

        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            parts.Add(bodyText.Trim());
        }

        return string.Join("\n", parts);
    }

    private static IReadOnlyList<ProjectMention> FindProjectMentions(SqliteConnection connection, string haystack)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY length(name) DESC;
            """;

        var mentions = new List<ProjectMention>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? null : reader.GetString(2);

            if (ContainsToken(haystack, name))
            {
                mentions.Add(new ProjectMention(id, name, code, 0.85));
            }
            else if (!string.IsNullOrWhiteSpace(code) && ContainsToken(haystack, code))
            {
                mentions.Add(new ProjectMention(id, name, code, 0.8));
            }
        }

        return mentions;
    }

    private static bool ContainsToken(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeActionableClaim(string haystack)
    {
        if (string.IsNullOrWhiteSpace(haystack) || haystack.Length < 12)
        {
            return false;
        }

        ReadOnlySpan<string> verbs =
        [
            "schedule", "confirm", "order", "call", "email", "follow up", "follow-up",
            "install", "need to", "please", "action", "todo", "to-do",
        ];
        foreach (var verb in verbs)
        {
            if (haystack.Contains(verb, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildExtractionSummary(string haystack, ProjectMention mention)
    {
        var sentence = FindSentenceMentioning(haystack, mention.Name)
            ?? (!string.IsNullOrWhiteSpace(mention.Code) ? FindSentenceMentioning(haystack, mention.Code!) : null)
            ?? haystack;

        sentence = sentence.Trim();
        if (sentence.Length > 200)
        {
            sentence = sentence[..200].TrimEnd() + "…";
        }

        return string.IsNullOrWhiteSpace(sentence)
            ? $"Claim for {mention.Name}"
            : sentence;
    }

    private static string? FindSentenceMentioning(string haystack, string token)
    {
        var parts = haystack.Split(['.', '!', '?', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return part;
            }
        }

        return null;
    }

    private static void EnsureProjectLink(SqliteConnection connection, string emailId, string projectId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at)
            VALUES ($id, $email, $project, $t)
            ON CONFLICT(email_artifact_id, project_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates an extraction for the project if none exists yet. Never overwrites or deletes
    /// extractions belonging to other projects.
    /// </summary>
    private static string? EnsureExtraction(
        SqliteConnection connection,
        string emailId,
        ProjectMention mention,
        string summary)
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText =
                """
                SELECT id FROM email_extractions
                WHERE email_artifact_id = $email
                  AND project_id = $p
                  AND archived_at IS NULL
                LIMIT 1;
                """;
            check.Parameters.AddWithValue("$email", emailId);
            check.Parameters.AddWithValue("$p", mention.Id);
            var existing = check.ExecuteScalar() as string;
            if (existing is not null)
            {
                return null;
            }
        }

        var workstreamId = FindDefaultWorkstream(connection, mention.Id);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO email_extractions (
              id, email_artifact_id, extraction_type, summary, project_id, workstream_id,
              confidence, created_at, updated_at)
            VALUES (
              $id, $email, 'action', $summary, $project, $ws, $confidence, $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$email", emailId);
        insert.Parameters.AddWithValue("$summary", summary);
        insert.Parameters.AddWithValue("$project", mention.Id);
        insert.Parameters.AddWithValue("$ws", (object?)workstreamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$confidence", mention.Confidence);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
        return id;
    }

    private static string? FindDefaultWorkstream(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM workstreams
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY priority ASC, created_at ASC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        return cmd.ExecuteScalar() as string;
    }

    private static bool EmailExists(SqliteConnection connection, string emailId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM email_artifacts WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", emailId);
        return cmd.ExecuteScalar() is not null;
    }

    private sealed record ProjectMention(string Id, string Name, string? Code, double Confidence);
}

public sealed class ClaimSplitResult
{
    public required IReadOnlyList<string> MentionedProjectIds { get; init; }

    public required IReadOnlyList<string> CreatedExtractionIds { get; init; }

    public required IReadOnlyList<string> LinkedProjectIds { get; init; }

    public string? SuggestionId { get; init; }

    public bool WasAmbiguous { get; init; }
}
