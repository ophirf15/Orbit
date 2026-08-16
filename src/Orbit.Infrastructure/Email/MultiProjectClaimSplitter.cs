using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Heuristic multi-project claim splitter (no LLM). When an email body mentions multiple
/// known project names/codes/aliases, ensures separate <c>email_extractions</c> per project.
/// Ambiguous action language with no clear project becomes an agent suggestion with ranked candidates.
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
        var ranked = ProjectIdentityMatcher.MatchHaystack(connection, haystack, max: 8);
        // Hard mentions: strong identity hits (name/code/alias), not weak name tokens alone.
        var mentions = ranked
            .Where(c => c.Score >= 0.8 && c.Reason is "name" or "code" or "alias")
            .ToList();
        var createdExtractions = new List<string>();
        var linkedProjects = new List<string>();
        var operatorChosenIds = LoadOperatorChosenProjectIds(connection, emailId);

        if (mentions.Count >= 1)
        {
            foreach (var mention in mentions)
            {
                // Explicit ingest pick wins — do not quietly attach other projects from name hits.
                if (operatorChosenIds.Count > 0
                    && !operatorChosenIds.Contains(mention.ProjectId, StringComparer.Ordinal))
                {
                    continue;
                }

                EnsureProjectLink(connection, emailId, mention);
                linkedProjects.Add(mention.ProjectId);

                var summary = BuildExtractionSummary(haystack, mention);
                var extractionId = EnsureExtraction(connection, emailId, mention, summary);
                if (extractionId is not null)
                {
                    createdExtractions.Add(extractionId);
                }
            }

            return new ClaimSplitResult
            {
                MentionedProjectIds = linkedProjects.Distinct(StringComparer.Ordinal).ToList(),
                CreatedExtractionIds = createdExtractions,
                LinkedProjectIds = linkedProjects.Distinct(StringComparer.Ordinal).ToList(),
                SuggestionId = null,
                WasAmbiguous = false,
            };
        }

        // Operator already picked a project at ingest — never ask again on Agent/Pulse.
        if (operatorChosenIds.Count > 0)
        {
            return new ClaimSplitResult
            {
                MentionedProjectIds = [],
                CreatedExtractionIds = [],
                LinkedProjectIds = operatorChosenIds,
                SuggestionId = null,
                WasAmbiguous = false,
            };
        }

        // No clear project name/code/alias — do not invent a hard extraction.
        string? suggestionId = null;
        if (LooksLikeActionableClaim(haystack))
        {
            suggestionId = EnsureAmbiguousSuggestion(emailId, haystack, ranked);
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

    /// <summary>Ingest / operator project pick — match_reason explicit or operator.</summary>
    internal static bool HasOperatorChosenProjectLink(SqliteConnection connection, string emailId) =>
        LoadOperatorChosenProjectIds(connection, emailId).Count > 0;

    private static List<string> LoadOperatorChosenProjectIds(SqliteConnection connection, string emailId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id
            FROM email_project_links
            WHERE email_artifact_id = $id
              AND lower(COALESCE(match_reason, '')) IN ('explicit', 'operator');
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private string? EnsureAmbiguousSuggestion(
        string emailId,
        string haystack,
        IReadOnlyList<ProjectMatchCandidate> ranked)
    {
        if (HasPendingDisambiguation(emailId))
        {
            return null;
        }

        var (subject, preview) = LoadEmailSubjectPreview(emailId);
        var snippet = BuildSnippet(subject, preview, haystack);
        var summary = BuildAmbiguousSummary(subject, snippet);
        var candidates = ranked
            .Where(c => c.Score >= ProjectIdentityMatcher.CandidateFloor)
            .Take(5)
            .Select(c => new
            {
                projectId = c.ProjectId,
                name = c.Name,
                score = c.Score,
                reason = c.Reason,
            })
            .ToArray();

        var payload = JsonSerializer.Serialize(new
        {
            action = SuggestionTypes.DisambiguateEmailClaim,
            emailId,
            subject,
            snippet,
            explanation = "Email claim has no clear project name/code/alias; do not silently assign.",
            evidence = new[] { "no_project_mention", $"snippet:{snippet}" },
            candidates,
        }, JsonOptions);

        var suggestion = _suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.DisambiguateEmailClaim,
            Summary = summary,
            PayloadJson = payload,
            GroupKey = SuggestionHygiene.DisambiguateEmailKey(emailId),
            Confidence = 0.35,
        });
        return suggestion.Id;
    }

    private (string? Subject, string? Preview) LoadEmailSubjectPreview(string emailId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT subject, body_preview
            FROM email_artifacts
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        var subject = reader.IsDBNull(0) ? null : reader.GetString(0);
        var preview = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (subject, preview);
    }

    internal static string BuildAmbiguousSummary(string? subject, string? snippet)
    {
        var subj = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        if (subj is not null)
        {
            if (subj.Length > 100)
            {
                subj = subj[..100].TrimEnd() + "…";
            }

            return $"Ambiguous email — “{subj}”";
        }

        if (!string.IsNullOrWhiteSpace(snippet))
        {
            var s = snippet.Trim();
            if (s.Length > 100)
            {
                s = s[..100].TrimEnd() + "…";
            }

            return $"Ambiguous email — {s}";
        }

        return "Ambiguous email claim — pick a project";
    }

    internal static string BuildSnippet(string? subject, string? preview, string? haystack)
    {
        // Prefer full haystack (subject+body) when present; else stored body preview.
        var raw = !string.IsNullOrWhiteSpace(haystack)
            ? haystack
            : preview;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var text = raw.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(subject)
            && text.StartsWith(subject.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            text = text[subject.Trim().Length..].TrimStart(' ', '-', ':', '|');
        }

        if (text.Length > 160)
        {
            text = text[..160].TrimEnd() + "…";
        }

        return text;
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

    private static string BuildExtractionSummary(string haystack, ProjectMatchCandidate mention)
    {
        var sentence = FindSentenceMentioning(haystack, mention.Name)
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

    private static void EnsureProjectLink(SqliteConnection connection, string emailId, ProjectMatchCandidate mention)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at, confidence, match_reason)
            VALUES ($id, $email, $project, $t, $confidence, $reason)
            ON CONFLICT(email_artifact_id, project_id) DO UPDATE SET
              confidence = COALESCE(excluded.confidence, email_project_links.confidence),
              match_reason = COALESCE(excluded.match_reason, email_project_links.match_reason);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$project", mention.ProjectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$confidence", mention.Score);
        cmd.Parameters.AddWithValue("$reason", mention.Reason);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates an extraction for the project if none exists yet. Never overwrites or deletes
    /// extractions belonging to other projects.
    /// </summary>
    private static string? EnsureExtraction(
        SqliteConnection connection,
        string emailId,
        ProjectMatchCandidate mention,
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
            check.Parameters.AddWithValue("$p", mention.ProjectId);
            var existing = check.ExecuteScalar() as string;
            if (existing is not null)
            {
                return null;
            }
        }

        var workstreamId = FindDefaultWorkstream(connection, mention.ProjectId);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO email_extractions (
              id, email_artifact_id, extraction_type, summary, project_id, workstream_id,
              confidence, match_reason, created_at, updated_at)
            VALUES (
              $id, $email, 'action', $summary, $project, $ws, $confidence, $reason, $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$email", emailId);
        insert.Parameters.AddWithValue("$summary", summary);
        insert.Parameters.AddWithValue("$project", mention.ProjectId);
        insert.Parameters.AddWithValue("$ws", (object?)workstreamId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$confidence", mention.Score);
        insert.Parameters.AddWithValue("$reason", mention.Reason);
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
}

public sealed class ClaimSplitResult
{
    public required IReadOnlyList<string> MentionedProjectIds { get; init; }

    public required IReadOnlyList<string> CreatedExtractionIds { get; init; }

    public required IReadOnlyList<string> LinkedProjectIds { get; init; }

    public string? SuggestionId { get; init; }

    public bool WasAmbiguous { get; init; }
}
