using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Malleability;

public sealed record LayoutDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string SchemaJson { get; init; }

    public required int Version { get; init; }

    public required bool IsActive { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }

    /// <summary>Layout target surface (default workbench).</summary>
    public string Target { get; init; } = "workbench";
}

public sealed class LayoutRevision
{
    public required string Id { get; init; }

    public required string LayoutId { get; init; }

    public required int Version { get; init; }

    public required string SchemaJson { get; init; }

    public required string CreatedAt { get; init; }
}

/// <summary>
/// Versioned layout/view definitions stored as JSON (lanes/filters/sections)
/// on Phase 4 <c>layout_definitions</c> plus <c>layout_revisions</c>.
/// </summary>
public sealed class LayoutStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly SqliteConnectionFactory _factory;

    public LayoutStore(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public IReadOnlyList<LayoutDefinition> List()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, definition_json, version, is_active, created_at, updated_at, target
            FROM layout_definitions
            WHERE archived_at IS NULL
            ORDER BY is_active DESC, updated_at DESC;
            """;
        var list = new List<LayoutDefinition>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadLayout(reader));
        }

        return list;
    }

    public LayoutDefinition? Get(string id)
    {
        using var connection = _factory.CreateConnection();
        return Get(connection, id);
    }

    public IReadOnlyList<LayoutRevision> ListRevisions(string layoutId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, layout_id, version, schema_json, created_at
            FROM layout_revisions
            WHERE layout_id = $id
            ORDER BY version DESC;
            """;
        cmd.Parameters.AddWithValue("$id", layoutId);
        var list = new List<LayoutRevision>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LayoutRevision
            {
                Id = reader.GetString(0),
                LayoutId = reader.GetString(1),
                Version = reader.GetInt32(2),
                SchemaJson = reader.GetString(3),
                CreatedAt = reader.GetString(4),
            });
        }

        return list;
    }

    public LayoutDefinition Save(
        string name,
        string schemaJson,
        string? layoutId = null,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name is required.", nameof(name));
        }

        var normalizedSchema = NormalizeSchemaJson(schemaJson);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        LayoutDefinition result;
        if (string.IsNullOrWhiteSpace(layoutId))
        {
            var id = Guid.NewGuid().ToString("D");
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    """
                    INSERT INTO layout_definitions (
                      id, name, target, definition_json, version, is_active, created_at, updated_at, archived_at)
                    VALUES (
                      $id, $name, 'workbench', $schema, 1, 0, $t, $t, NULL);
                    """;
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$name", name.Trim());
                insert.Parameters.AddWithValue("$schema", normalizedSchema);
                insert.Parameters.AddWithValue("$t", now);
                insert.ExecuteNonQuery();
            }

            InsertRevision(connection, tx, id, 1, normalizedSchema, now);
            result = new LayoutDefinition
            {
                Id = id,
                Name = name.Trim(),
                SchemaJson = normalizedSchema,
                Version = 1,
                IsActive = false,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        else
        {
            var existing = Get(connection, layoutId.Trim(), tx)
                ?? throw new ArgumentException("Layout was not found.", nameof(layoutId));

            var nextVersion = existing.Version + 1;
            using (var update = connection.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText =
                    """
                    UPDATE layout_definitions
                    SET name = $name, definition_json = $schema, version = $version, updated_at = $t
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$name", name.Trim());
                update.Parameters.AddWithValue("$schema", normalizedSchema);
                update.Parameters.AddWithValue("$version", nextVersion);
                update.Parameters.AddWithValue("$t", now);
                update.Parameters.AddWithValue("$id", existing.Id);
                update.ExecuteNonQuery();
            }

            InsertRevision(connection, tx, existing.Id, nextVersion, normalizedSchema, now);
            result = new LayoutDefinition
            {
                Id = existing.Id,
                Name = name.Trim(),
                SchemaJson = normalizedSchema,
                Version = nextVersion,
                IsActive = existing.IsActive,
                CreatedAt = existing.CreatedAt,
                UpdatedAt = now,
                Target = existing.Target,
            };
        }

        WriteAudit(
            connection,
            tx,
            "layout.saved",
            actor ?? "agent",
            new { layoutId = result.Id, version = result.Version, name = result.Name },
            provenance);

        tx.Commit();
        return result;
    }

    public LayoutDefinition Apply(string layoutId, string? actor = null, MutationProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(layoutId))
        {
            throw new ArgumentException("layoutId is required.", nameof(layoutId));
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        var layout = Get(connection, layoutId.Trim(), tx)
            ?? throw new ArgumentException("Layout was not found.", nameof(layoutId));

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "UPDATE layout_definitions SET is_active = 0 WHERE is_active = 1;";
            clear.ExecuteNonQuery();
        }

        using (var activate = connection.CreateCommand())
        {
            activate.Transaction = tx;
            activate.CommandText =
                """
                UPDATE layout_definitions
                SET is_active = 1, updated_at = $t
                WHERE id = $id;
                """;
            activate.Parameters.AddWithValue("$t", now);
            activate.Parameters.AddWithValue("$id", layout.Id);
            activate.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "layout.applied",
            actor ?? "agent",
            new { layoutId = layout.Id, version = layout.Version },
            provenance);

        tx.Commit();

        return layout with { IsActive = true, UpdatedAt = now };
    }

    public LayoutDefinition Revert(
        string layoutId,
        int? toVersion = null,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        if (string.IsNullOrWhiteSpace(layoutId))
        {
            throw new ArgumentException("layoutId is required.", nameof(layoutId));
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        var layout = Get(connection, layoutId.Trim(), tx)
            ?? throw new ArgumentException("Layout was not found.", nameof(layoutId));

        int targetVersion;
        string schemaJson;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            if (toVersion is int explicitVersion)
            {
                cmd.CommandText =
                    """
                    SELECT version, schema_json FROM layout_revisions
                    WHERE layout_id = $id AND version = $version;
                    """;
                cmd.Parameters.AddWithValue("$id", layout.Id);
                cmd.Parameters.AddWithValue("$version", explicitVersion);
            }
            else
            {
                if (layout.Version <= 1)
                {
                    throw new ArgumentException("No prior version to revert to.");
                }

                cmd.CommandText =
                    """
                    SELECT version, schema_json FROM layout_revisions
                    WHERE layout_id = $id AND version = $version;
                    """;
                cmd.Parameters.AddWithValue("$id", layout.Id);
                cmd.Parameters.AddWithValue("$version", layout.Version - 1);
            }

            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentException("Target layout revision was not found.");
            }

            targetVersion = reader.GetInt32(0);
            schemaJson = reader.GetString(1);
        }

        var nextVersion = layout.Version + 1;
        using (var update = connection.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE layout_definitions
                SET definition_json = $schema, version = $version, updated_at = $t
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$schema", schemaJson);
            update.Parameters.AddWithValue("$version", nextVersion);
            update.Parameters.AddWithValue("$t", now);
            update.Parameters.AddWithValue("$id", layout.Id);
            update.ExecuteNonQuery();
        }

        InsertRevision(connection, tx, layout.Id, nextVersion, schemaJson, now);

        WriteAudit(
            connection,
            tx,
            "layout.reverted",
            actor ?? "agent",
            new
            {
                layoutId = layout.Id,
                restoredFromVersion = targetVersion,
                newVersion = nextVersion,
            },
            provenance);

        tx.Commit();

        return new LayoutDefinition
        {
            Id = layout.Id,
            Name = layout.Name,
            SchemaJson = schemaJson,
            Version = nextVersion,
            IsActive = layout.IsActive,
            CreatedAt = layout.CreatedAt,
            UpdatedAt = now,
            Target = layout.Target,
        };
    }

    private static LayoutDefinition? Get(SqliteConnection connection, string id, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        cmd.CommandText =
            """
            SELECT id, name, definition_json, version, is_active, created_at, updated_at, target
            FROM layout_definitions
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadLayout(reader) : null;
    }

    private static LayoutDefinition ReadLayout(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            SchemaJson = reader.GetString(2),
            Version = reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
            IsActive = !reader.IsDBNull(4) && reader.GetInt32(4) == 1,
            CreatedAt = reader.GetString(5),
            UpdatedAt = reader.GetString(6),
            Target = reader.IsDBNull(7) ? "workbench" : reader.GetString(7),
        };

    private static void InsertRevision(
        SqliteConnection connection,
        SqliteTransaction tx,
        string layoutId,
        int version,
        string schemaJson,
        string createdAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO layout_revisions (id, layout_id, version, schema_json, created_at)
            VALUES ($id, $layout, $version, $schema, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$layout", layoutId);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$schema", schemaJson);
        cmd.Parameters.AddWithValue("$t", createdAt);
        cmd.ExecuteNonQuery();
    }

    private static string NormalizeSchemaJson(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            throw new ArgumentException("schemaJson is required.", nameof(schemaJson));
        }

        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("schemaJson must be a JSON object.");
            }

            return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("schemaJson must be valid JSON.", nameof(schemaJson), ex);
        }
    }

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string actor,
        object detail,
        MutationProvenance? provenance)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO audit_events (id, event_type, entity_type, entity_id, actor, detail_json, created_at)
            VALUES ($id, $eventType, $entityType, $entityId, $actor, $detail, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$eventType", eventType);
        cmd.Parameters.AddWithValue("$entityType", "layout");
        cmd.Parameters.AddWithValue("$entityId", DBNull.Value);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}
