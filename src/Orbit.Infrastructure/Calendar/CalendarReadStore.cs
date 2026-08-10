using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Calendar;

public sealed class CalendarSourceRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Provider { get; init; }

    public string? MailboxName { get; init; }

    public string? CalendarName { get; init; }

    public string? AccountHint { get; init; }

    public string? ConfigUri { get; init; }

    public bool Enabled { get; init; }

    public string? LastSyncAt { get; init; }

    public string? LastSyncStatus { get; init; }

    public string? LastSyncError { get; init; }
}

public sealed class CalendarContextMeeting
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? StartsAt { get; init; }

    public string? EndsAt { get; init; }

    public string? Location { get; init; }

    public double? AttentionScore { get; init; }

    public string? SourceId { get; init; }

    public string? SourceName { get; init; }

    public string? MailboxName { get; init; }

    public string? CalendarName { get; init; }

    /// <summary>Organizer identity string as ingested from the provider (name/address). No full attendee list yet.</summary>
    public string? Organizer { get; init; }

    public string? UpdatedAt { get; init; }

    public IReadOnlyList<CalendarLinkedEntity> LinkedEntities { get; init; } = [];
}

public sealed class CalendarLinkedEntity
{
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public string? Label { get; init; }

    public double? Confidence { get; init; }
}

public sealed class CalendarReadStore
{
    private readonly SqliteConnectionFactory _factory;

    public CalendarReadStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<CalendarSourceRecord> ListSources()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, provider, mailbox_name, calendar_name, account_hint, config_uri,
                   enabled, last_sync_at, last_sync_status, last_sync_error
            FROM calendar_sources
            WHERE archived_at IS NULL
            ORDER BY name COLLATE NOCASE;
            """;
        var list = new List<CalendarSourceRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CalendarSourceRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Provider = reader.IsDBNull(2) ? null : reader.GetString(2),
                MailboxName = reader.IsDBNull(3) ? null : reader.GetString(3),
                CalendarName = reader.IsDBNull(4) ? null : reader.GetString(4),
                AccountHint = reader.IsDBNull(5) ? null : reader.GetString(5),
                ConfigUri = reader.IsDBNull(6) ? null : reader.GetString(6),
                Enabled = !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                LastSyncAt = reader.IsDBNull(8) ? null : reader.GetString(8),
                LastSyncStatus = reader.IsDBNull(9) ? null : reader.GetString(9),
                LastSyncError = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return list;
    }

    public IReadOnlyList<CalendarContextMeeting> GetUpcomingContext(
        TimeSpan? window = null,
        int limit = 40,
        DateTimeOffset? changedSince = null)
    {
        // existing method continues below — ListHighAttention added after GetMeetingsForProject
        var horizon = window ?? TimeSpan.FromDays(14);
        var now = DateTime.UtcNow;
        var until = now.Add(horizon);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.id, e.title, e.starts_at, e.ends_at, e.location, e.attention_score,
                   e.calendar_source_id, s.name, s.mailbox_name, s.calendar_name, e.organizer, e.updated_at
            FROM calendar_events e
            LEFT JOIN calendar_sources s ON s.id = e.calendar_source_id
            WHERE e.archived_at IS NULL
              AND e.starts_at IS NOT NULL
              AND e.starts_at >= $now
              AND e.starts_at <= $until
              AND ($changedSince IS NULL OR e.updated_at >= $changedSince)
            ORDER BY e.starts_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$now", now.ToString("O"));
        cmd.Parameters.AddWithValue("$until", until.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$changedSince", (object?)changedSince?.UtcDateTime.ToString("O") ?? DBNull.Value);

        var meetings = new List<CalendarContextMeeting>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            meetings.Add(new CalendarContextMeeting
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                StartsAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                EndsAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                Location = reader.IsDBNull(4) ? null : reader.GetString(4),
                AttentionScore = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                SourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceName = reader.IsDBNull(7) ? null : reader.GetString(7),
                MailboxName = reader.IsDBNull(8) ? null : reader.GetString(8),
                CalendarName = reader.IsDBNull(9) ? null : reader.GetString(9),
                Organizer = reader.IsDBNull(10) ? null : reader.GetString(10),
                UpdatedAt = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        reader.Close();

        return meetings.Select(m => new CalendarContextMeeting
        {
            Id = m.Id,
            Title = m.Title,
            StartsAt = m.StartsAt,
            EndsAt = m.EndsAt,
            Location = m.Location,
            AttentionScore = m.AttentionScore,
            SourceId = m.SourceId,
            SourceName = m.SourceName,
            MailboxName = m.MailboxName,
            CalendarName = m.CalendarName,
            Organizer = m.Organizer,
            UpdatedAt = m.UpdatedAt,
            LinkedEntities = LoadLinks(connection, m.Id),
        }).ToList();
    }

    public IReadOnlyList<CalendarContextMeeting> GetMeetingsForProject(string projectId, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.id, e.title, e.starts_at, e.ends_at, e.location, e.attention_score,
                   e.calendar_source_id, s.name, s.mailbox_name, s.calendar_name
            FROM calendar_events e
            INNER JOIN event_entity_links l
              ON l.calendar_event_id = e.id
             AND l.entity_type = 'project'
             AND l.entity_id = $p
            LEFT JOIN calendar_sources s ON s.id = e.calendar_source_id
            WHERE e.archived_at IS NULL
            ORDER BY COALESCE(e.starts_at, e.created_at) ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var meetings = new List<CalendarContextMeeting>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            meetings.Add(new CalendarContextMeeting
            {
                Id = id,
                Title = reader.GetString(1),
                StartsAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                EndsAt = reader.IsDBNull(3) ? null : reader.GetString(3),
                Location = reader.IsDBNull(4) ? null : reader.GetString(4),
                AttentionScore = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                SourceId = reader.IsDBNull(6) ? null : reader.GetString(6),
                SourceName = reader.IsDBNull(7) ? null : reader.GetString(7),
                MailboxName = reader.IsDBNull(8) ? null : reader.GetString(8),
                CalendarName = reader.IsDBNull(9) ? null : reader.GetString(9),
            });
        }

        reader.Close();
        return meetings.Select(m => new CalendarContextMeeting
        {
            Id = m.Id,
            Title = m.Title,
            StartsAt = m.StartsAt,
            EndsAt = m.EndsAt,
            Location = m.Location,
            AttentionScore = m.AttentionScore,
            SourceId = m.SourceId,
            SourceName = m.SourceName,
            MailboxName = m.MailboxName,
            CalendarName = m.CalendarName,
            LinkedEntities = LoadLinks(connection, m.Id),
        }).ToList();
    }

    /// <summary>Upcoming meetings with attention_score above a threshold (duty operator wake).</summary>
    public IReadOnlyList<CalendarContextMeeting> ListHighAttention(int limit = 5, double minScore = 0.55)
    {
        return GetUpcomingContext(TimeSpan.FromDays(3), Math.Max(limit * 4, 20))
            .Where(m => (m.AttentionScore ?? 0) >= minScore)
            .OrderByDescending(m => m.AttentionScore ?? 0)
            .Take(Math.Clamp(limit, 1, 20))
            .ToList();
    }

    private static IReadOnlyList<CalendarLinkedEntity> LoadLinks(SqliteConnection connection, string eventId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT l.entity_type, l.entity_id, l.confidence,
                   CASE
                     WHEN l.entity_type = 'project' THEN (SELECT name FROM projects WHERE id = l.entity_id)
                     ELSE NULL
                   END AS label
            FROM event_entity_links l
            WHERE l.calendar_event_id = $e;
            """;
        cmd.Parameters.AddWithValue("$e", eventId);
        var list = new List<CalendarLinkedEntity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new CalendarLinkedEntity
            {
                EntityType = reader.GetString(0),
                EntityId = reader.GetString(1),
                Confidence = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                Label = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }
}
