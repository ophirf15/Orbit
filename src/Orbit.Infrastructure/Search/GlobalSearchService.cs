using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Search;

public sealed class GlobalSearchHit
{
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Title { get; init; }

    public required string Snippet { get; init; }

    public required double Score { get; init; }

    public string? ProjectId { get; init; }

    public string? Path { get; init; }

    public string? PreviewKind { get; init; }
}

/// <summary>
/// FTS-backed unified search across graph entities, files, emails, calendar, and conversations.
/// Ranking: match quality + recency + attention + optional focus/meeting boost.
/// </summary>
public sealed class GlobalSearchService
{
    private readonly SqliteConnectionFactory _factory;

    public GlobalSearchService(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<GlobalSearchHit> Search(
        string query,
        string? focusProjectId = null,
        string? focusMeetingId = null,
        int limit = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        limit = Math.Clamp(limit, 1, 100);

        var focusProject = string.IsNullOrWhiteSpace(focusProjectId) ? null : focusProjectId.Trim();
        var focusMeeting = string.IsNullOrWhiteSpace(focusMeetingId) ? null : focusMeetingId.Trim();
        var focusEntityKeys = LoadMeetingFocusKeys(focusMeeting);

        using var connection = _factory.CreateConnection();

        try
        {
            return SearchFts(connection, query.Trim(), focusProject, focusEntityKeys, limit);
        }
        catch (SqliteException)
        {
            return SearchLike(connection, query.Trim(), focusProject, focusEntityKeys, limit);
        }
    }

    private static HashSet<string> LoadMeetingFocusKeys(string? focusMeetingId)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (focusMeetingId is null)
        {
            return keys;
        }

        // Meeting itself is a focus signal.
        keys.Add("calendar_event:" + focusMeetingId);
        return keys;
    }

    private HashSet<string> ExpandMeetingLinks(SqliteConnection connection, string? focusMeetingId)
    {
        var keys = LoadMeetingFocusKeys(focusMeetingId);
        if (focusMeetingId is null)
        {
            return keys;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT entity_type, entity_id
            FROM event_entity_links
            WHERE calendar_event_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", focusMeetingId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var type = NormalizeType(reader.GetString(0));
            keys.Add(type + ":" + reader.GetString(1));
        }

        return keys;
    }

    private IReadOnlyList<GlobalSearchHit> SearchFts(
        SqliteConnection connection,
        string query,
        string? focusProjectId,
        HashSet<string> seedFocusKeys,
        int limit)
    {
        var focusKeys = ExpandMeetingLinks(connection, ExtractMeetingId(seedFocusKeys));
        foreach (var k in seedFocusKeys)
        {
            focusKeys.Add(k);
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT sd.entity_type, sd.entity_id, sd.title, sd.body, sd.project_id, sd.updated_at,
                   bm25(search_documents_fts) AS match_rank,
                   CASE sd.entity_type
                     WHEN 'task' THEN (SELECT t.attention_score FROM tasks t WHERE t.id = sd.entity_id)
                     WHEN 'workstream' THEN (SELECT w.attention_score FROM workstreams w WHERE w.id = sd.entity_id)
                     WHEN 'calendar_event' THEN (SELECT e.attention_score FROM calendar_events e WHERE e.id = sd.entity_id)
                     ELSE NULL
                   END AS attention,
                   CASE
                     WHEN sd.entity_type = 'file' THEN (SELECT fa.path FROM file_artifacts fa WHERE fa.id = sd.entity_id)
                     ELSE NULL
                   END AS file_path
            FROM search_documents_fts fts
            INNER JOIN search_documents sd ON sd.rowid = fts.rowid
            WHERE fts MATCH $q
            LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$q", ToFtsQuery(query));

        var hits = new List<GlobalSearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(ScoreHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                focusProjectId,
                focusKeys,
                isBm25: true));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
    }

    private IReadOnlyList<GlobalSearchHit> SearchLike(
        SqliteConnection connection,
        string query,
        string? focusProjectId,
        HashSet<string> seedFocusKeys,
        int limit)
    {
        var focusKeys = ExpandMeetingLinks(connection, ExtractMeetingId(seedFocusKeys));
        foreach (var k in seedFocusKeys)
        {
            focusKeys.Add(k);
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT sd.entity_type, sd.entity_id, sd.title, sd.body, sd.project_id, sd.updated_at,
                   0.0 AS match_rank,
                   CASE sd.entity_type
                     WHEN 'task' THEN (SELECT t.attention_score FROM tasks t WHERE t.id = sd.entity_id)
                     WHEN 'workstream' THEN (SELECT w.attention_score FROM workstreams w WHERE w.id = sd.entity_id)
                     WHEN 'calendar_event' THEN (SELECT e.attention_score FROM calendar_events e WHERE e.id = sd.entity_id)
                     ELSE NULL
                   END AS attention,
                   CASE
                     WHEN sd.entity_type = 'file' THEN (SELECT fa.path FROM file_artifacts fa WHERE fa.id = sd.entity_id)
                     ELSE NULL
                   END AS file_path
            FROM search_documents sd
            WHERE sd.title LIKE $like ESCAPE '\'
               OR sd.body LIKE $like ESCAPE '\'
            LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$like", "%" + EscapeLike(query) + "%");

        var hits = new List<GlobalSearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(ScoreHit(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                matchRank: ExactnessBonus(reader.GetString(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3), query),
                attention: reader.IsDBNull(7) ? null : reader.GetDouble(7),
                filePath: reader.IsDBNull(8) ? null : reader.GetString(8),
                focusProjectId,
                focusKeys,
                isBm25: false));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
    }

    private static GlobalSearchHit ScoreHit(
        string entityType,
        string entityId,
        string title,
        string body,
        string? projectId,
        string? updatedAt,
        double matchRank,
        double? attention,
        string? filePath,
        string? focusProjectId,
        HashSet<string> focusKeys,
        bool isBm25)
    {
        // bm25: more negative is better → invert. LIKE path: matchRank is already a positive bonus.
        var matchScore = isBm25 ? Math.Max(0, 12.0 + (-matchRank)) : matchRank;
        var recency = RecencyBoost(updatedAt);
        var attentionBoost = attention is null ? 0 : Math.Clamp(attention.Value, 0, 1) * 2.0;
        var focusBoost = 0.0;

        if (focusProjectId is not null
            && string.Equals(projectId, focusProjectId, StringComparison.Ordinal))
        {
            focusBoost += 3.0;
        }

        var key = NormalizeType(entityType) + ":" + entityId;
        if (focusKeys.Contains(key)
            || (focusProjectId is not null && focusKeys.Contains("project:" + focusProjectId)
                && string.Equals(projectId, focusProjectId, StringComparison.Ordinal)))
        {
            focusBoost += 2.5;
        }

        if (focusKeys.Contains(NormalizeType(entityType) + ":" + entityId))
        {
            focusBoost += 1.5;
        }

        var score = matchScore + recency + attentionBoost + focusBoost;
        return new GlobalSearchHit
        {
            EntityType = entityType,
            EntityId = entityId,
            Title = title,
            Snippet = MakeSnippet(body, title),
            Score = Math.Round(score, 4),
            ProjectId = projectId,
            Path = filePath,
            PreviewKind = entityType switch
            {
                "file" => "file",
                "email" => "email",
                _ => null,
            },
        };
    }

    private static string? ExtractMeetingId(HashSet<string> keys)
    {
        foreach (var key in keys)
        {
            if (key.StartsWith("calendar_event:", StringComparison.Ordinal))
            {
                return key["calendar_event:".Length..];
            }
        }

        return null;
    }

    private static string NormalizeType(string entityType) => entityType switch
    {
        "email_artifact" => "email",
        "file_artifact" => "file",
        _ => entityType,
    };

    private static double RecencyBoost(string? updatedAtIso)
    {
        if (string.IsNullOrWhiteSpace(updatedAtIso)
            || !DateTime.TryParse(updatedAtIso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var updated))
        {
            return 0;
        }

        var ageDays = Math.Max(0, (DateTime.UtcNow - updated.ToUniversalTime()).TotalDays);
        return Math.Max(0, 2.0 - (ageDays / 30.0));
    }

    private static double ExactnessBonus(string title, string body, string query)
    {
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 8;
        }

        if (body.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        return 2;
    }

    private static string MakeSnippet(string body, string title)
    {
        var text = string.IsNullOrWhiteSpace(body) ? title : body;
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= 180 ? text : text[..177] + "...";
    }

    private static string ToFtsQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return "\"\"";
        }

        // Prefix-friendly fragments: (token* OR "token") per term, AND across terms.
        return string.Join(
            " AND ",
            tokens.Select(t =>
            {
                var escaped = t.Replace("\"", "\"\"", StringComparison.Ordinal);
                var bare = new string(escaped.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
                if (bare.Length >= 2)
                {
                    return $"({bare}* OR \"{escaped}\")";
                }

                return $"\"{escaped}\"";
            }));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
