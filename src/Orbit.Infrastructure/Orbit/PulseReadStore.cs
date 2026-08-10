using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Pulse;

public sealed class PulseSnapshotRecord
{
    public required string Id { get; init; }

    public string? DayBrief { get; init; }

    public string? PayloadJson { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class PulseConcernRecord
{
    public required string TaskId { get; init; }

    public required string ProjectId { get; init; }

    public required string ProjectName { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public string? NextAction { get; init; }

    public string? BodyExcerpt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class OrbitProjectRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Summary { get; init; }

    public required string Status { get; init; }

    public bool InOrbit { get; init; }

    public int OpenConcernCount { get; init; }

    public string? TopNextAction { get; init; }
}

public sealed class PulseView
{
    public string? DayBrief { get; init; }

    public string? HermesHint { get; init; }

    public required string GeneratedAt { get; init; }

    public required IReadOnlyList<PulseConcernRecord> Concerns { get; init; }

    public bool BriefIsSynthetic { get; init; }
}

public sealed class PulseReadStore
{
    public const string IgnitionCompletedKey = "ignition_completed";

    private const int BodyExcerptMaxLength = 240;

    private readonly SqliteConnectionFactory _factory;

    public PulseReadStore(SqliteConnectionFactory factory) => _factory = factory;

    public PulseView GetPulse()
    {
        var snapshot = GetLatestSnapshot();
        var generatedAt = snapshot?.CreatedAt ?? DateTimeOffset.UtcNow.ToString("O");
        var concerns = LoadConcerns();
        var brief = snapshot?.DayBrief;
        var synthetic = false;
        if (string.IsNullOrWhiteSpace(brief))
        {
            brief = SynthesizeDayBrief(concerns);
            synthetic = true;
        }

        return new PulseView
        {
            DayBrief = brief,
            HermesHint = synthetic
                ? "Orbit drafted this from open concerns. Ask Hermes (Telegram or Agent) to refresh the living brief."
                : "Hermes pulse refresh available via /v1/pulse/refresh.",
            GeneratedAt = generatedAt,
            Concerns = concerns,
            BriefIsSynthetic = synthetic,
        };
    }

    private static string SynthesizeDayBrief(IReadOnlyList<PulseConcernRecord> concerns)
    {
        if (concerns.Count == 0)
        {
            return "Nothing open in the orbit yet. Ask Hermes to add a project or a next concern — Pulse is the feed Hermes fills for you.";
        }

        var top = concerns.Take(3).ToList();
        var lines = new List<string>
        {
            $"You have {concerns.Count} open concern{(concerns.Count == 1 ? "" : "s")}. Focus here first:",
        };
        foreach (var c in top)
        {
            var next = string.IsNullOrWhiteSpace(c.NextAction) ? c.Title : c.NextAction;
            lines.Add($"• {c.ProjectName}: {next}");
        }

        if (concerns.Count > top.Count)
        {
            lines.Add($"…and {concerns.Count - top.Count} more below.");
        }

        return string.Join("\n", lines);
    }

    public PulseSnapshotRecord? GetLatestSnapshot()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, day_brief, payload_json, created_at
            FROM pulse_snapshots
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapSnapshot(reader) : null;
    }

    public PulseSnapshotRecord SaveSnapshot(string? dayBrief, string? payloadJson)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO pulse_snapshots (id, day_brief, payload_json, created_at)
            VALUES ($id, $brief, $payload, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$brief", (object?)dayBrief ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();

        return new PulseSnapshotRecord
        {
            Id = id,
            DayBrief = dayBrief,
            PayloadJson = payloadJson,
            CreatedAt = now,
        };
    }

    public IReadOnlyList<OrbitProjectRecord> ListOrbitProjects()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.name, p.summary, p.status, p.in_orbit,
                   (
                     SELECT COUNT(*) FROM tasks t
                     WHERE t.project_id = p.id
                       AND t.archived_at IS NULL
                       AND t.status NOT IN ($complete, $archived)
                   ) AS open_count,
                   (
                     SELECT t.next_action FROM tasks t
                     WHERE t.project_id = p.id
                       AND t.archived_at IS NULL
                       AND t.status NOT IN ($complete, $archived)
                     ORDER BY t.updated_at DESC
                     LIMIT 1
                   ) AS top_next
            FROM projects p
            WHERE p.archived_at IS NULL AND p.in_orbit = 1
            ORDER BY p.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
        cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
        return ReadOrbitProjects(cmd);
    }

    public IReadOnlyList<OrbitProjectRecord> ListActiveProjects()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.name, p.summary, p.status, p.in_orbit,
                   (
                     SELECT COUNT(*) FROM tasks t
                     WHERE t.project_id = p.id
                       AND t.archived_at IS NULL
                       AND t.status NOT IN ($complete, $archived)
                   ) AS open_count,
                   (
                     SELECT t.next_action FROM tasks t
                     WHERE t.project_id = p.id
                       AND t.archived_at IS NULL
                       AND t.status NOT IN ($complete, $archived)
                     ORDER BY t.updated_at DESC
                     LIMIT 1
                   ) AS top_next
            FROM projects p
            WHERE p.archived_at IS NULL
            ORDER BY p.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
        cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
        return ReadOrbitProjects(cmd);
    }

    public bool HasAnyInOrbit()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM projects
            WHERE archived_at IS NULL AND in_orbit = 1
            LIMIT 1;
            """;
        return cmd.ExecuteScalar() is not null;
    }

    public void SetInOrbit(string projectId, bool inOrbit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE projects
            SET in_orbit = $flag, updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        cmd.Parameters.AddWithValue("$flag", inOrbit ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", now);
        var rows = cmd.ExecuteNonQuery();
        if (rows == 0)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }

    public void SetAllInOrbit(IEnumerable<string> projectIds)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        var ids = projectIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText =
                """
                UPDATE projects
                SET in_orbit = 0, updated_at = $t
                WHERE archived_at IS NULL AND in_orbit = 1;
                """;
            clear.Parameters.AddWithValue("$t", now);
            clear.ExecuteNonQuery();
        }

        foreach (var id in ids)
        {
            using var set = connection.CreateCommand();
            set.Transaction = tx;
            set.CommandText =
                """
                UPDATE projects
                SET in_orbit = 1, updated_at = $t
                WHERE id = $id AND archived_at IS NULL;
                """;
            set.Parameters.AddWithValue("$id", id);
            set.Parameters.AddWithValue("$t", now);
            set.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public string? FindProjectIdByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM projects
            WHERE archived_at IS NULL AND name = $name COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$name", name.Trim());
        return cmd.ExecuteScalar() as string;
    }

    public bool IsIgnitionCompleted() =>
        string.Equals(GetSetting(IgnitionCompletedKey), "1", StringComparison.Ordinal)
        || string.Equals(GetSetting(IgnitionCompletedKey), "true", StringComparison.OrdinalIgnoreCase);

    public void SetIgnitionCompleted(bool completed) =>
        SetSetting(IgnitionCompletedKey, completed ? "1" : "0");

    public string? GetSetting(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM orbit_settings WHERE key = $key LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key.Trim());
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO orbit_settings (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$key", key.Trim());
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Same concerns as <see cref="GetPulse"/>, but sorted by task id (not <c>updated_at</c>) so the
    /// ordering never shifts on a touch that doesn't change semantic content — used by
    /// <c>/v1/agent/snapshot</c> for stable hashing.
    /// </summary>
    public IReadOnlyList<PulseConcernRecord> GetConcernsSortedById() =>
        LoadConcerns()
            .OrderBy(c => c.TaskId, StringComparer.Ordinal)
            .ToList();

    private IReadOnlyList<PulseConcernRecord> LoadConcerns()
    {
        var scopeInOrbit = HasAnyInOrbit();
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.project_id, p.name, t.title, t.status, t.next_action, t.body, t.updated_at
            FROM tasks t
            INNER JOIN projects p ON p.id = t.project_id
            WHERE t.archived_at IS NULL
              AND p.archived_at IS NULL
              AND t.status NOT IN ($complete, $archived)
              AND ($scopeAll = 1 OR p.in_orbit = 1)
            ORDER BY t.updated_at DESC;
            """;
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
        cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
        cmd.Parameters.AddWithValue("$scopeAll", scopeInOrbit ? 0 : 1);
        return ReadConcerns(cmd);
    }

    private static List<PulseConcernRecord> ReadConcerns(SqliteCommand cmd)
    {
        var list = new List<PulseConcernRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var body = reader.IsDBNull(6) ? null : reader.GetString(6);
            list.Add(new PulseConcernRecord
            {
                TaskId = reader.GetString(0),
                ProjectId = reader.GetString(1),
                ProjectName = reader.GetString(2),
                Title = reader.GetString(3),
                Status = reader.GetString(4),
                NextAction = reader.IsDBNull(5) ? null : reader.GetString(5),
                BodyExcerpt = ExcerptBody(body),
                UpdatedAt = reader.GetString(7),
            });
        }

        return list;
    }

    private static List<OrbitProjectRecord> ReadOrbitProjects(SqliteCommand cmd)
    {
        var list = new List<OrbitProjectRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OrbitProjectRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Summary = reader.IsDBNull(2) ? null : reader.GetString(2),
                Status = reader.GetString(3),
                InOrbit = !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                OpenConcernCount = reader.FieldCount > 5 && !reader.IsDBNull(5) ? (int)reader.GetInt64(5) : 0,
                TopNextAction = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null,
            });
        }

        return list;
    }

    private static PulseSnapshotRecord MapSnapshot(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        DayBrief = reader.IsDBNull(1) ? null : reader.GetString(1),
        PayloadJson = reader.IsDBNull(2) ? null : reader.GetString(2),
        CreatedAt = reader.GetString(3),
    };

    private static string? ExcerptBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        if (trimmed.Length <= BodyExcerptMaxLength)
        {
            return trimmed;
        }

        return trimmed[..BodyExcerptMaxLength].TrimEnd() + "…";
    }

    public static string BuildConfirmPayloadJson(IReadOnlyList<OrbitProjectRecord> projects) =>
        JsonSerializer.Serialize(new
        {
            kind = "orbit.ignition.confirmed",
            projectCount = projects.Count,
            projects = projects.Select(p => new { id = p.Id, name = p.Name }).ToList(),
        });
}
