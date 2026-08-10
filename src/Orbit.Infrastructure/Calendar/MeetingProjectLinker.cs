using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Calendar;

/// <summary>
/// Heuristic event→project linker: project name (or code) appears in subject/body/location.
/// Persists confidence + provenance on event_entity_links. Never mutates Priority.
/// </summary>
public sealed class MeetingProjectLinker
{
    private readonly SqliteConnectionFactory _factory;

    public MeetingProjectLinker(SqliteConnectionFactory factory) => _factory = factory;

    public int LinkAll()
    {
        using var connection = _factory.CreateConnection();
        var projects = LoadProjects(connection);
        if (projects.Count == 0)
        {
            return 0;
        }

        var created = 0;
        using var tx = connection.BeginTransaction();
        foreach (var ev in LoadEvents(connection, tx))
        {
            var haystack = $"{ev.Title}\n{ev.BodyPreview}\n{ev.Location}";
            foreach (var project in projects)
            {
                if (!ContainsToken(haystack, project.Name)
                    && (string.IsNullOrWhiteSpace(project.Code) || !ContainsToken(haystack, project.Code!)))
                {
                    continue;
                }

                var confidence = ContainsToken(ev.Title, project.Name) ? 0.9 : 0.7;
                if (EnsureLink(connection, tx, ev.Id, project.Id, confidence, "subject_body_name_match"))
                {
                    created++;
                }
            }
        }

        tx.Commit();
        return created;
    }

    private static bool ContainsToken(string? haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool EnsureLink(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventId,
        string projectId,
        double confidence,
        string provenance)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = tx;
            exists.CommandText =
                """
                SELECT id FROM event_entity_links
                WHERE calendar_event_id = $e
                  AND entity_type = $t
                  AND entity_id = $p
                LIMIT 1;
                """;
            exists.Parameters.AddWithValue("$e", eventId);
            exists.Parameters.AddWithValue("$t", EntityTypes.Project);
            exists.Parameters.AddWithValue("$p", projectId);
            var existing = exists.ExecuteScalar() as string;
            if (existing is not null)
            {
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText =
                    """
                    UPDATE event_entity_links
                    SET confidence = $c, provenance = $prov
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$id", existing);
                update.Parameters.AddWithValue("$c", confidence);
                update.Parameters.AddWithValue("$prov", provenance);
                update.ExecuteNonQuery();
                return false;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO event_entity_links (
              id, calendar_event_id, entity_type, entity_id, confidence, provenance, created_at)
            VALUES ($id, $e, $t, $p, $c, $prov, $now);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$e", eventId);
        insert.Parameters.AddWithValue("$t", EntityTypes.Project);
        insert.Parameters.AddWithValue("$p", projectId);
        insert.Parameters.AddWithValue("$c", confidence);
        insert.Parameters.AddWithValue("$prov", provenance);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
        return true;
    }

    private static IReadOnlyList<(string Id, string Name, string? Code)> LoadProjects(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code
            FROM projects
            WHERE archived_at IS NULL;
            """;
        var list = new List<(string, string, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return list;
    }

    private static IReadOnlyList<(string Id, string Title, string? BodyPreview, string? Location)> LoadEvents(
        SqliteConnection connection,
        SqliteTransaction tx)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT id, title, body_preview, location
            FROM calendar_events
            WHERE archived_at IS NULL;
            """;
        var list = new List<(string, string, string?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return list;
    }
}
