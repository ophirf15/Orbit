using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Changes;

public sealed class ChangeLogEntry
{
    public long Revision { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string ChangeKind { get; init; }

    public string? SourceEvent { get; init; }

    public bool Tombstone { get; init; }

    public string? ChangedFieldsJson { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ChangeLogStore
{
    private readonly SqliteConnectionFactory _factory;

    public ChangeLogStore(SqliteConnectionFactory factory) => _factory = factory;

    public long Append(
        string entityType,
        string entityId,
        string changeKind,
        string? sourceEvent = null,
        bool tombstone = false,
        string? changedFieldsJson = null)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO orbit_change_log
              (entity_type, entity_id, change_kind, source_event, tombstone, changed_fields_json, created_at)
            VALUES
              ($type, $id, $kind, $source, $tomb, $fields, $at);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$type", entityType);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.Parameters.AddWithValue("$kind", changeKind);
        cmd.Parameters.AddWithValue("$source", (object?)sourceEvent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$tomb", tombstone ? 1 : 0);
        cmd.Parameters.AddWithValue("$fields", (object?)changedFieldsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public (IReadOnlyList<ChangeLogEntry> Events, long NextCursor) ListSince(long cursor, int limit = 200)
    {
        var take = Math.Clamp(limit, 1, 500);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT revision, entity_type, entity_id, change_kind, source_event, tombstone, changed_fields_json, created_at
            FROM orbit_change_log
            WHERE revision > $cursor
            ORDER BY revision ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cursor", cursor);
        cmd.Parameters.AddWithValue("$limit", take);

        var list = ReadEntries(cmd);
        var next = list.Count == 0 ? cursor : list[^1].Revision;
        return (list, next);
    }

    /// <summary>Newest-first change log rows for one entity (uses idx_orbit_change_log_entity).</summary>
    public IReadOnlyList<ChangeLogEntry> ListForEntity(string entityType, string entityId, int limit = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        var take = Math.Clamp(limit, 1, 300);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT revision, entity_type, entity_id, change_kind, source_event, tombstone, changed_fields_json, created_at
            FROM orbit_change_log
            WHERE entity_type = $type AND entity_id = $id
            ORDER BY revision DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$type", entityType.Trim());
        cmd.Parameters.AddWithValue("$id", entityId.Trim());
        cmd.Parameters.AddWithValue("$limit", take);
        return ReadEntries(cmd);
    }

    private static List<ChangeLogEntry> ReadEntries(SqliteCommand cmd)
    {
        var list = new List<ChangeLogEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ChangeLogEntry
            {
                Revision = reader.GetInt64(0),
                EntityType = reader.GetString(1),
                EntityId = reader.GetString(2),
                ChangeKind = reader.GetString(3),
                SourceEvent = reader.IsDBNull(4) ? null : reader.GetString(4),
                Tombstone = reader.GetInt64(5) != 0,
                ChangedFieldsJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetString(7),
            });
        }

        return list;
    }

    public long CurrentCursor()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(revision), 0) FROM orbit_change_log;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
