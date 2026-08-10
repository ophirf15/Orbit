using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Calendar;

/// <summary>
/// Derived attention scoring for calendar events. Never rewrites tasks.priority / workstreams.priority.
/// </summary>
public sealed class AttentionScorer
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Func<DateTimeOffset> _clock;

    public AttentionScorer(SqliteConnectionFactory factory, Func<DateTimeOffset>? clock = null)
    {
        _factory = factory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int RescoreAll()
    {
        using var connection = _factory.CreateConnection();
        var now = _clock();
        var openBlockerProjects = LoadOpenBlockerProjects(connection);
        var updated = 0;

        using var tx = connection.BeginTransaction();
        using (var list = connection.CreateCommand())
        {
            list.Transaction = tx;
            list.CommandText =
                """
                SELECT e.id, e.starts_at, l.entity_id
                FROM calendar_events e
                LEFT JOIN event_entity_links l
                  ON l.calendar_event_id = e.id
                 AND l.entity_type = 'project'
                WHERE e.archived_at IS NULL;
                """;

            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            using var reader = list.ExecuteReader();
            while (reader.Read())
            {
                var eventId = reader.GetString(0);
                DateTimeOffset? starts = null;
                if (!reader.IsDBNull(1)
                    && DateTimeOffset.TryParse(reader.GetString(1), out var parsed))
                {
                    starts = parsed.ToUniversalTime();
                }

                var projectId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var score = ScoreImminence(starts, now);
                if (projectId is not null && openBlockerProjects.Contains(projectId))
                {
                    score = Math.Min(1.0, score + 0.15);
                }

                if (!scores.TryGetValue(eventId, out var existing) || score > existing)
                {
                    scores[eventId] = score;
                }
            }

            reader.Close();

            foreach (var (eventId, score) in scores)
            {
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText =
                    """
                    UPDATE calendar_events
                    SET attention_score = $s, updated_at = $t
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$s", score);
                update.Parameters.AddWithValue("$t", now.UtcDateTime.ToString("O"));
                update.Parameters.AddWithValue("$id", eventId);
                update.ExecuteNonQuery();
                updated++;
            }
        }

        tx.Commit();
        return updated;
    }

    /// <summary>Explainable imminence curve used by tests and UI notes.</summary>
    public static double ScoreImminence(DateTimeOffset? startsAt, DateTimeOffset now)
    {
        if (startsAt is null)
        {
            return 0.05;
        }

        var delta = startsAt.Value - now;
        if (delta < TimeSpan.Zero)
        {
            // Recently started / just ended still holds mild attention for an hour.
            return delta > TimeSpan.FromHours(-1) ? 0.5 : 0.0;
        }

        if (delta <= TimeSpan.FromHours(2))
        {
            return 1.0;
        }

        if (delta <= TimeSpan.FromHours(24))
        {
            return 0.85;
        }

        if (delta <= TimeSpan.FromHours(72))
        {
            return 0.65;
        }

        if (delta <= TimeSpan.FromDays(7))
        {
            return 0.45;
        }

        if (delta <= TimeSpan.FromDays(14))
        {
            return 0.25;
        }

        return 0.1;
    }

    private static HashSet<string> LoadOpenBlockerProjects(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT project_id
            FROM blockers
            WHERE status = 'open' AND project_id IS NOT NULL;
            """;
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                set.Add(reader.GetString(0));
            }
        }

        return set;
    }
}
