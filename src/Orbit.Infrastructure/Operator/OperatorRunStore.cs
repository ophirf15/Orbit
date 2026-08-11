using Microsoft.Data.Sqlite;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Operator;

public sealed class OperatorRunRecord
{
    public required string Id { get; init; }

    public required string TriggerKind { get; init; }

    public string? TriggerPayloadJson { get; init; }

    public string? HermesSessionId { get; init; }

    public string? HermesRunId { get; init; }

    public required string Status { get; init; }

    public string? BriefingSummary { get; init; }

    public string? ErrorText { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }

    public string? CompletedAt { get; init; }
}

public sealed class OperatorRunStore
{
    private readonly SqliteConnectionFactory _factory;

    public OperatorRunStore(SqliteConnectionFactory factory) => _factory = factory;

    public OperatorRunRecord Start(string triggerKind, string? triggerPayloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerKind);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO operator_runs (
              id, trigger_kind, trigger_payload_json, status, created_at, updated_at)
            VALUES ($id, $trigger, $payload, $status, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$trigger", triggerKind);
        cmd.Parameters.AddWithValue("$payload", (object?)triggerPayloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", OperatorRunStatuses.Running);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return Get(id)!;
    }

    public OperatorRunRecord? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, trigger_kind, trigger_payload_json, hermes_session_id, hermes_run_id,
                   status, briefing_summary, error_text, created_at, updated_at, completed_at
            FROM operator_runs
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public IReadOnlyList<OperatorRunRecord> ListRecent(int limit = 20)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, trigger_kind, trigger_payload_json, hermes_session_id, hermes_run_id,
                   status, briefing_summary, error_text, created_at, updated_at, completed_at
            FROM operator_runs
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        return ReadAll(cmd);
    }

    /// <summary>
    /// Live status while status stays <c>running</c> (surfaced on the Workbench duty banner).
    /// Overwrites <see cref="OperatorRunRecord.BriefingSummary"/> until <see cref="Complete"/>.
    /// </summary>
    public OperatorRunRecord? SetProgress(string id, string progressText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.IsNullOrWhiteSpace(progressText))
        {
            return Get(id);
        }

        var now = DateTime.UtcNow.ToString("O");
        var text = progressText.Trim();
        if (text.Length > 400)
        {
            text = text[..400] + "…";
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE operator_runs
            SET briefing_summary = $briefing,
                updated_at = $t
            WHERE id = $id AND status = $status;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$briefing", text);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$status", OperatorRunStatuses.Running);
        if (cmd.ExecuteNonQuery() == 0)
        {
            return null;
        }

        return Get(id);
    }

    public OperatorRunRecord? Complete(
        string id,
        string status,
        string? briefingSummary = null,
        string? errorText = null,
        string? hermesSessionId = null,
        string? hermesRunId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE operator_runs
            SET status = $status,
                briefing_summary = COALESCE($briefing, briefing_summary),
                error_text = $error,
                hermes_session_id = COALESCE($session, hermes_session_id),
                hermes_run_id = COALESCE($run, hermes_run_id),
                updated_at = $t,
                completed_at = $t
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$briefing", (object?)briefingSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)errorText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$session", (object?)hermesSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$run", (object?)hermesRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        if (cmd.ExecuteNonQuery() == 0)
        {
            return null;
        }

        return Get(id);
    }

    public DateTimeOffset? LastCompletedUtc(string? triggerKind = null)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(triggerKind))
        {
            cmd.CommandText =
                """
                SELECT completed_at FROM operator_runs
                WHERE completed_at IS NOT NULL AND status IN ('completed', 'failed', 'skipped')
                ORDER BY completed_at DESC
                LIMIT 1;
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT completed_at FROM operator_runs
                WHERE completed_at IS NOT NULL
                  AND trigger_kind = $trigger
                  AND status IN ('completed', 'failed', 'skipped')
                ORDER BY completed_at DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$trigger", triggerKind);
        }

        var value = cmd.ExecuteScalar() as string;
        return DateTimeOffset.TryParse(value, out var dto) ? dto : null;
    }

    public int CountRunning()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operator_runs WHERE status = $status;";
        cmd.Parameters.AddWithValue("$status", OperatorRunStatuses.Running);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Force-complete runs stuck in <c>running</c> longer than <paramref name="maxAge"/>.
    /// Without this, a single hung calendar/email run permanently blocks <see cref="CountRunning"/> (MaxConcurrentRuns=1).
    /// Pass <see cref="TimeSpan.Zero"/> (or negative) to abandon <b>all</b> running rows (Host restart recovery).
    /// </summary>
    public int AbandonStaleRunning(TimeSpan maxAge, string reason = "Abandoned stale operator run.")
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        if (maxAge <= TimeSpan.Zero)
        {
            cmd.CommandText =
                """
                UPDATE operator_runs
                SET status = $status,
                    briefing_summary = COALESCE(NULLIF(TRIM(briefing_summary), ''), $briefing),
                    error_text = COALESCE(error_text, $error),
                    updated_at = $t,
                    completed_at = $t
                WHERE status = $running;
                """;
        }
        else
        {
            var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("O");
            cmd.CommandText =
                """
                UPDATE operator_runs
                SET status = $status,
                    briefing_summary = COALESCE(NULLIF(TRIM(briefing_summary), ''), $briefing),
                    error_text = COALESCE(error_text, $error),
                    updated_at = $t,
                    completed_at = $t
                WHERE status = $running
                  AND created_at < $cutoff;
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
        }

        cmd.Parameters.AddWithValue("$status", OperatorRunStatuses.Failed);
        cmd.Parameters.AddWithValue("$briefing", reason);
        cmd.Parameters.AddWithValue("$error", reason);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$running", OperatorRunStatuses.Running);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Abandon every <c>running</c> row — used on Host startup after crash/restart.</summary>
    public int AbandonAllRunning(string reason = "Cleared on Host startup (previous session interrupted).") =>
        AbandonStaleRunning(TimeSpan.Zero, reason);

    /// <summary>Newest running run for a trigger, optionally matching an email id in the payload.</summary>
    public OperatorRunRecord? FindRunning(string triggerKind, string? emailId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerKind);
        foreach (var run in ListRecent(40))
        {
            if (!string.Equals(run.Status, OperatorRunStatuses.Running, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(run.TriggerKind, triggerKind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(emailId)
                && (run.TriggerPayloadJson is null
                    || !run.TriggerPayloadJson.Contains(emailId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return run;
        }

        return null;
    }

    private static List<OperatorRunRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<OperatorRunRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OperatorRunRecord
            {
                Id = reader.GetString(0),
                TriggerKind = reader.GetString(1),
                TriggerPayloadJson = reader.IsDBNull(2) ? null : reader.GetString(2),
                HermesSessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                HermesRunId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Status = reader.GetString(5),
                BriefingSummary = reader.IsDBNull(6) ? null : reader.GetString(6),
                ErrorText = reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAt = reader.GetString(8),
                UpdatedAt = reader.GetString(9),
                CompletedAt = reader.IsDBNull(10) ? null : reader.GetString(10),
            });
        }

        return list;
    }
}
