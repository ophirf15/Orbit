using Microsoft.Data.Sqlite;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Operator;

public sealed class OperatorMemoryRecord
{
    public required string Id { get; init; }

    public required string Scope { get; init; }

    public required string Kind { get; init; }

    public required string Text { get; init; }

    public string? EvidenceRefsJson { get; init; }

    public double? Confidence { get; init; }

    public string? Source { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class RememberRequest
{
    public required string Text { get; init; }

    public required string Kind { get; init; }

    public string Scope { get; init; } = "global";

    public string? EvidenceRefsJson { get; init; }

    public double? Confidence { get; init; }

    public string? Source { get; init; }
}

public sealed class OperatorMemoryStore
{
    private readonly SqliteConnectionFactory _factory;

    public OperatorMemoryStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<OperatorMemoryRecord> List(string? scope = null, int limit = 100)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        var take = Math.Clamp(limit, 1, 500);
        if (string.IsNullOrWhiteSpace(scope))
        {
            cmd.CommandText =
                """
                SELECT id, scope, kind, text, evidence_refs_json, confidence, source, created_at, updated_at
                FROM operator_memory
                WHERE archived_at IS NULL
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT id, scope, kind, text, evidence_refs_json, confidence, source, created_at, updated_at
                FROM operator_memory
                WHERE archived_at IS NULL AND (scope = $scope OR scope = 'global')
                ORDER BY CASE WHEN scope = $scope THEN 0 ELSE 1 END, updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$scope", scope.Trim());
        }

        cmd.Parameters.AddWithValue("$limit", take);
        return ReadAll(cmd);
    }

    public OperatorMemoryRecord Remember(RememberRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        if (!OperatorMemoryKinds.All.Contains(request.Kind))
        {
            throw new ArgumentException("Unknown memory kind.", nameof(request));
        }

        var scope = string.IsNullOrWhiteSpace(request.Scope) ? "global" : request.Scope.Trim();
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO operator_memory (
              id, scope, kind, text, evidence_refs_json, confidence, source, created_at, updated_at)
            VALUES (
              $id, $scope, $kind, $text, $evidence, $confidence, $source, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$kind", request.Kind);
        cmd.Parameters.AddWithValue("$text", request.Text.Trim());
        cmd.Parameters.AddWithValue("$evidence", (object?)request.EvidenceRefsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$confidence", (object?)request.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$source", (object?)request.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public OperatorMemoryRecord? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, scope, kind, text, evidence_refs_json, confidence, source, created_at, updated_at
            FROM operator_memory
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public bool Forget(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE operator_memory
            SET archived_at = $t, updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    private static List<OperatorMemoryRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<OperatorMemoryRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OperatorMemoryRecord
            {
                Id = reader.GetString(0),
                Scope = reader.GetString(1),
                Kind = reader.GetString(2),
                Text = reader.GetString(3),
                EvidenceRefsJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                Confidence = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Source = reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAt = reader.GetString(7),
                UpdatedAt = reader.GetString(8),
            });
        }

        return list;
    }
}
