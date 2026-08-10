using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Operator;

public sealed class OperatorRuleRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public bool Enabled { get; init; }

    public required string TriggerKind { get; init; }

    public string? MatchJson { get; init; }

    public required string ActionKind { get; init; }

    public string? ParamsJson { get; init; }

    public bool RequireConfirm { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class CreateOperatorRuleRequest
{
    public required string Name { get; init; }

    public required string TriggerKind { get; init; }

    public required string ActionKind { get; init; }

    public string? MatchJson { get; init; }

    public string? ParamsJson { get; init; }

    public bool Enabled { get; init; } = true;

    public bool RequireConfirm { get; init; }
}

public sealed class StandingRulesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnectionFactory _factory;

    public StandingRulesStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<OperatorRuleRecord> List(bool enabledOnly = false, int limit = 200)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        var take = Math.Clamp(limit, 1, 500);
        cmd.CommandText = enabledOnly
            ? """
              SELECT id, name, enabled, trigger_kind, match_json, action_kind, params_json,
                     require_confirm, created_at, updated_at
              FROM operator_rules
              WHERE archived_at IS NULL AND enabled = 1
              ORDER BY updated_at DESC
              LIMIT $limit;
              """
            : """
              SELECT id, name, enabled, trigger_kind, match_json, action_kind, params_json,
                     require_confirm, created_at, updated_at
              FROM operator_rules
              WHERE archived_at IS NULL
              ORDER BY updated_at DESC
              LIMIT $limit;
              """;
        cmd.Parameters.AddWithValue("$limit", take);
        return ReadAll(cmd);
    }

    public OperatorRuleRecord? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, enabled, trigger_kind, match_json, action_kind, params_json,
                   require_confirm, created_at, updated_at
            FROM operator_rules
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public OperatorRuleRecord Create(CreateOperatorRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        if (!OperatorTriggers.All.Contains(request.TriggerKind))
        {
            throw new ArgumentException("Unknown trigger kind.", nameof(request));
        }

        if (!OperatorActions.All.Contains(request.ActionKind))
        {
            throw new ArgumentException("Unknown action kind.", nameof(request));
        }

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO operator_rules (
              id, name, enabled, trigger_kind, match_json, action_kind, params_json,
              require_confirm, created_at, updated_at)
            VALUES (
              $id, $name, $enabled, $trigger, $match, $action, $params,
              $confirm, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", request.Name.Trim());
        cmd.Parameters.AddWithValue("$enabled", request.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$trigger", request.TriggerKind);
        cmd.Parameters.AddWithValue("$match", (object?)request.MatchJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$action", request.ActionKind);
        cmd.Parameters.AddWithValue("$params", (object?)request.ParamsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$confirm", request.RequireConfirm ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public OperatorRuleRecord? SetEnabled(string id, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE operator_rules
            SET enabled = $enabled, updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() == 0)
        {
            return null;
        }

        return Get(id);
    }

    public bool Archive(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE operator_rules
            SET archived_at = $t, updated_at = $t, enabled = 0
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// Returns enabled rules for <paramref name="triggerKind"/> whose match JSON is empty
    /// or whose projectId / orgId / subjectContains fields match the context.
    /// </summary>
    public IReadOnlyList<OperatorRuleRecord> Match(string triggerKind, OperatorMatchContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerKind);
        ArgumentNullException.ThrowIfNull(context);
        return List(enabledOnly: true)
            .Where(r => string.Equals(r.TriggerKind, triggerKind, StringComparison.Ordinal))
            .Where(r => Matches(r.MatchJson, context))
            .ToList();
    }

    public static bool Matches(string? matchJson, OperatorMatchContext context)
    {
        if (string.IsNullOrWhiteSpace(matchJson))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(matchJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("projectId", out var projectEl))
            {
                var want = projectEl.GetString();
                if (!string.IsNullOrWhiteSpace(want)
                    && !string.Equals(want, context.ProjectId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (root.TryGetProperty("orgId", out var orgEl))
            {
                var want = orgEl.GetString();
                if (!string.IsNullOrWhiteSpace(want)
                    && !string.Equals(want, context.OrgId, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (root.TryGetProperty("subjectContains", out var subEl))
            {
                var needle = subEl.GetString();
                if (!string.IsNullOrWhiteSpace(needle))
                {
                    var hay = context.Subject ?? string.Empty;
                    if (!hay.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            if (root.TryGetProperty("suggestionType", out var sugEl))
            {
                var want = sugEl.GetString();
                if (!string.IsNullOrWhiteSpace(want)
                    && !string.Equals(want, context.SuggestionType, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<OperatorRuleRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<OperatorRuleRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OperatorRuleRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Enabled = reader.GetInt32(2) != 0,
                TriggerKind = reader.GetString(3),
                MatchJson = reader.IsDBNull(4) ? null : reader.GetString(4),
                ActionKind = reader.GetString(5),
                ParamsJson = reader.IsDBNull(6) ? null : reader.GetString(6),
                RequireConfirm = reader.GetInt32(7) != 0,
                CreatedAt = reader.GetString(8),
                UpdatedAt = reader.GetString(9),
            });
        }

        return list;
    }
}

public sealed class OperatorMatchContext
{
    public string? ProjectId { get; init; }

    public string? OrgId { get; init; }

    public string? Subject { get; init; }

    public string? SuggestionType { get; init; }

    public string? EmailId { get; init; }

    public string? TaskId { get; init; }
}
