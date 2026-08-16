using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

public sealed class TaskDependencyRecord
{
    public required string Id { get; init; }

    public required string PredecessorTaskId { get; init; }

    public required string SuccessorTaskId { get; init; }

    public required string DependencyType { get; init; }

    /// <summary>Why the two tasks are linked.</summary>
    public string? Reason { get; init; }

    /// <summary>What the successor is waiting on from the predecessor (e.g. "line count").</summary>
    public string? Expects { get; init; }

    public double? Confidence { get; init; }

    public string? EvidenceRef { get; init; }

    public string? FollowUpAt { get; init; }

    public string? Cadence { get; init; }

    public string? SatisfiedAt { get; init; }

    public required string CreatedBy { get; init; }

    public required string CreatedAt { get; init; }

    public bool IsExplicitlySatisfied => !string.IsNullOrWhiteSpace(SatisfiedAt);
}

/// <summary>A dependency joined with the counterpart task, from one task's point of view.</summary>
public sealed class TaskDependencyEdge
{
    public required TaskDependencyRecord Dependency { get; init; }

    /// <summary>True when the anchor task is the successor (i.e. it is waiting on the other task).</summary>
    public required bool AnchorIsSuccessor { get; init; }

    public required string OtherTaskId { get; init; }

    public required string OtherTaskTitle { get; init; }

    public required string OtherTaskStatus { get; init; }

    public string? OtherTaskProjectId { get; init; }

    public string? OtherTaskNextAction { get; init; }

    /// <summary>The other task has reached a terminal state, so a gating edge is satisfied.</summary>
    public bool OtherTaskIsDone =>
        string.Equals(OtherTaskStatus, TaskStatuses.Complete, StringComparison.Ordinal)
        || string.Equals(OtherTaskStatus, TaskStatuses.Archived, StringComparison.Ordinal);

    /// <summary>Satisfied via predecessor done or explicit clear-with-evidence.</summary>
    public bool IsSatisfied => OtherTaskIsDone || Dependency.IsExplicitlySatisfied;
}

/// <summary>A gating dependency whose predecessor is finished while the successor is still open.</summary>
public sealed class TaskDependencyReadyRow
{
    public required TaskDependencyRecord Dependency { get; init; }

    public required string PredecessorTitle { get; init; }

    public required string PredecessorStatus { get; init; }

    public string? PredecessorNextAction { get; init; }

    public string? PredecessorBody { get; init; }

    public required string SuccessorTitle { get; init; }

    public required string SuccessorStatus { get; init; }

    public string? SuccessorProjectId { get; init; }
}

/// <summary>
/// Directional task-to-task dependencies (predecessor → successor). Deduplicated, cycle-guarded,
/// and hard-deleted on unlink so the 0001 UNIQUE constraint stays re-linkable.
/// </summary>
public sealed class TaskDependencyStore
{
    private const string SelectColumns =
        "id, predecessor_task_id, successor_task_id, dependency_type, reason, expects, confidence, evidence_ref, created_by, created_at, follow_up_at, cadence, satisfied_at";

    private const string SelectColumnsAliasD =
        "d.id, d.predecessor_task_id, d.successor_task_id, d.dependency_type, d.reason, d.expects, d.confidence, d.evidence_ref, d.created_by, d.created_at, d.follow_up_at, d.cadence, d.satisfied_at";

    private readonly SqliteConnectionFactory _factory;

    public TaskDependencyStore(SqliteConnectionFactory factory) => _factory = factory;

    public TaskDependencyRecord Link(
        string predecessorTaskId,
        string successorTaskId,
        string? dependencyType = null,
        string? reason = null,
        string? expects = null,
        double? confidence = null,
        string? evidenceRef = null,
        string? followUpAt = null,
        string? cadence = null,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(predecessorTaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(successorTaskId);

        var predecessor = predecessorTaskId.Trim();
        var successor = successorTaskId.Trim();
        if (string.Equals(predecessor, successor, StringComparison.Ordinal))
        {
            throw new ArgumentException("A task cannot depend on itself.", nameof(successorTaskId));
        }

        var type = NormalizeType(dependencyType);
        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        var id = LinkCore(
            connection,
            tx,
            predecessor,
            successor,
            type,
            reason,
            expects,
            confidence,
            evidenceRef,
            followUpAt,
            cadence,
            requestedBy,
            now);

        WriteAudit(
            connection,
            tx,
            "task.dependency.linked",
            id,
            requestedBy,
            new
            {
                predecessorTaskId = predecessor,
                successorTaskId = successor,
                dependencyType = type,
                reason = Trimmed(reason),
                expects = Trimmed(expects),
                evidenceRef = Trimmed(evidenceRef),
                followUpAt = Trimmed(followUpAt),
                cadence = Trimmed(cadence),
            },
            now,
            provenance);

        tx.Commit();
        return Get(id) ?? throw new InvalidOperationException("Dependency was not readable after insert.");
    }

    internal static string LinkCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        string predecessorTaskId,
        string successorTaskId,
        string? dependencyType,
        string? reason,
        string? expects,
        double? confidence,
        string? evidenceRef,
        string requestedBy,
        string now) =>
        LinkCore(
            connection,
            tx,
            predecessorTaskId,
            successorTaskId,
            dependencyType,
            reason,
            expects,
            confidence,
            evidenceRef,
            followUpAt: null,
            cadence: null,
            requestedBy,
            now);

    /// <summary>
    /// Validates and upserts one dependency edge inside an existing transaction, returning its id.
    /// Shared with suggestion-accept so both paths get the same dedupe and cycle guard.
    /// </summary>
    internal static string LinkCore(
        SqliteConnection connection,
        SqliteTransaction tx,
        string predecessorTaskId,
        string successorTaskId,
        string? dependencyType,
        string? reason,
        string? expects,
        double? confidence,
        string? evidenceRef,
        string? followUpAt,
        string? cadence,
        string requestedBy,
        string now)
    {
        var predecessor = predecessorTaskId.Trim();
        var successor = successorTaskId.Trim();
        if (string.Equals(predecessor, successor, StringComparison.Ordinal))
        {
            throw new ArgumentException("A task cannot depend on itself.", nameof(successorTaskId));
        }

        var type = NormalizeType(dependencyType);
        EnsureTaskExists(connection, tx, predecessor, nameof(predecessorTaskId));
        EnsureTaskExists(connection, tx, successor, nameof(successorTaskId));

        var existing = FindEdge(connection, tx, predecessor, successor, type);
        if (existing is not null)
        {
            // Idempotent: enrich a previously thin edge instead of duplicating it.
            if (reason is not null || expects is not null || confidence is not null || evidenceRef is not null
                || followUpAt is not null || cadence is not null)
            {
                using var enrich = connection.CreateCommand();
                enrich.Transaction = tx;
                enrich.CommandText =
                    """
                    UPDATE task_dependencies
                    SET reason = COALESCE($reason, reason),
                        expects = COALESCE($expects, expects),
                        confidence = COALESCE($confidence, confidence),
                        evidence_ref = COALESCE($evidence, evidence_ref),
                        follow_up_at = COALESCE($follow, follow_up_at),
                        cadence = COALESCE($cadence, cadence),
                        updated_at = $t
                    WHERE id = $id;
                    """;
                enrich.Parameters.AddWithValue("$reason", (object?)Trimmed(reason) ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$expects", (object?)Trimmed(expects) ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$confidence", (object?)confidence ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$evidence", (object?)Trimmed(evidenceRef) ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$follow", (object?)Trimmed(followUpAt) ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$cadence", (object?)Trimmed(cadence) ?? DBNull.Value);
                enrich.Parameters.AddWithValue("$t", now);
                enrich.Parameters.AddWithValue("$id", existing.Id);
                enrich.ExecuteNonQuery();
            }

            return existing.Id;
        }

        // Reverse edge of the same pair would make the ordering ambiguous.
        if (FindEdge(connection, tx, successor, predecessor, type) is not null)
        {
            throw new ArgumentException(
                "The reverse dependency already exists; remove it before linking this direction.",
                nameof(predecessorTaskId));
        }

        if (TaskDependencyTypes.IsGating(type)
            && WouldCreateCycle(connection, tx, predecessor, successor))
        {
            throw new ArgumentException(
                "That link would create a circular dependency.",
                nameof(successorTaskId));
        }

        var id = Guid.NewGuid().ToString("D");
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO task_dependencies (
              id, predecessor_task_id, successor_task_id, dependency_type,
              reason, expects, confidence, evidence_ref, follow_up_at, cadence,
              created_by, created_at, updated_at)
            VALUES (
              $id, $pred, $succ, $type,
              $reason, $expects, $confidence, $evidence, $follow, $cadence,
              $by, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$pred", predecessor);
        cmd.Parameters.AddWithValue("$succ", successor);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$reason", (object?)Trimmed(reason) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$expects", (object?)Trimmed(expects) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$confidence", (object?)confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$evidence", (object?)Trimmed(evidenceRef) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$follow", (object?)Trimmed(followUpAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cadence", (object?)Trimmed(cadence) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$by", requestedBy);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>
    /// Marks a dependency waiting edge satisfied with evidence without deleting the link.
    /// Preserves expects/reason text.
    /// </summary>
    public TaskDependencyRecord Satisfy(
        string dependencyId,
        string evidenceRef,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceRef);
        var evidence = Trimmed(evidenceRef)
            ?? throw new ArgumentException("evidenceRef is required.", nameof(evidenceRef));

        var existing = Get(dependencyId)
            ?? throw new ArgumentException("Dependency was not found.", nameof(dependencyId));

        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                UPDATE task_dependencies
                SET satisfied_at = $sat,
                    evidence_ref = $ev,
                    updated_at = $t
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$sat", now);
            cmd.Parameters.AddWithValue("$ev", evidence);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$id", existing.Id);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "task.dependency.satisfied",
            existing.Id,
            requestedBy,
            new
            {
                evidenceRef = evidence,
                expects = existing.Expects,
                predecessorTaskId = existing.PredecessorTaskId,
                successorTaskId = existing.SuccessorTaskId,
            },
            now,
            provenance);
        tx.Commit();

        return Get(existing.Id) ?? throw new InvalidOperationException("Dependency was not readable after satisfy.");
    }

    public bool Unlink(string dependencyId, string? actor = null, MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);
        var existing = Get(dependencyId);
        if (existing is null)
        {
            return false;
        }

        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM task_dependencies WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", existing.Id);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "task.dependency.unlinked",
            existing.Id,
            requestedBy,
            new
            {
                predecessorTaskId = existing.PredecessorTaskId,
                successorTaskId = existing.SuccessorTaskId,
                dependencyType = existing.DependencyType,
            },
            now,
            provenance);

        tx.Commit();
        return true;
    }

    public TaskDependencyRecord? Get(string dependencyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM task_dependencies WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", dependencyId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>All dependency edges touching a task, in both directions, with counterpart task details.</summary>
    public IReadOnlyList<TaskDependencyEdge> ListForTask(string taskId, int limit = 40)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var take = Math.Clamp(limit, 1, 200);
        var edges = new List<TaskDependencyEdge>();

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT {SelectColumnsAliasD},
                   CASE WHEN d.successor_task_id = $id THEN 1 ELSE 0 END AS anchor_is_successor,
                   other.id, other.title, other.status, other.project_id, other.next_action
            FROM task_dependencies d
            INNER JOIN tasks other
              ON other.id = CASE WHEN d.successor_task_id = $id
                                 THEN d.predecessor_task_id
                                 ELSE d.successor_task_id END
            WHERE (d.predecessor_task_id = $id OR d.successor_task_id = $id)
              AND other.archived_at IS NULL
            ORDER BY anchor_is_successor DESC, d.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$id", taskId.Trim());
        cmd.Parameters.AddWithValue("$limit", take);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new TaskDependencyEdge
            {
                Dependency = ReadRecord(reader),
                AnchorIsSuccessor = reader.GetInt64(13) == 1,
                OtherTaskId = reader.GetString(14),
                OtherTaskTitle = reader.GetString(15),
                OtherTaskStatus = reader.GetString(16),
                OtherTaskProjectId = reader.IsDBNull(17) ? null : reader.GetString(17),
                OtherTaskNextAction = reader.IsDBNull(18) ? null : reader.GetString(18),
            });
        }

        return edges;
    }

    /// <summary>
    /// Gating edges whose predecessor is finished while the successor is still open —
    /// the "your blocker cleared, can we close this?" monitor feed.
    /// </summary>
    public IReadOnlyList<TaskDependencyReadyRow> ListReadyDependencies(int limit = 50)
    {
        var take = Math.Clamp(limit, 1, 200);
        var rows = new List<TaskDependencyReadyRow>();

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            SELECT {SelectColumnsAliasD},
                   p.title, p.status, p.next_action, p.body,
                   s.title, s.status, s.project_id
            FROM task_dependencies d
            INNER JOIN tasks p ON p.id = d.predecessor_task_id
            INNER JOIN tasks s ON s.id = d.successor_task_id
            WHERE d.dependency_type IN ($blocks, $informs)
              AND d.satisfied_at IS NULL
              AND p.archived_at IS NULL
              AND s.archived_at IS NULL
              AND p.status = $complete
              AND s.status NOT IN ($complete, $archived)
            ORDER BY d.created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$blocks", TaskDependencyTypes.Blocks);
        cmd.Parameters.AddWithValue("$informs", TaskDependencyTypes.Informs);
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
        cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
        cmd.Parameters.AddWithValue("$limit", take);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TaskDependencyReadyRow
            {
                Dependency = ReadRecord(reader),
                PredecessorTitle = reader.GetString(13),
                PredecessorStatus = reader.GetString(14),
                PredecessorNextAction = reader.IsDBNull(15) ? null : reader.GetString(15),
                PredecessorBody = reader.IsDBNull(16) ? null : reader.GetString(16),
                SuccessorTitle = reader.GetString(17),
                SuccessorStatus = reader.GetString(18),
                SuccessorProjectId = reader.IsDBNull(19) ? null : reader.GetString(19),
            });
        }

        return rows;
    }

    private static void EnsureTaskExists(
        SqliteConnection connection,
        SqliteTransaction tx,
        string taskId,
        string paramName)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM tasks WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", taskId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Task was not found.", paramName);
        }
    }

    private static TaskDependencyRecord? FindEdge(
        SqliteConnection connection,
        SqliteTransaction tx,
        string predecessor,
        string successor,
        string type)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            $"""
            SELECT {SelectColumns} FROM task_dependencies
            WHERE predecessor_task_id = $pred
              AND successor_task_id = $succ
              AND dependency_type = $type
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$pred", predecessor);
        cmd.Parameters.AddWithValue("$succ", successor);
        cmd.Parameters.AddWithValue("$type", type);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    /// <summary>
    /// True when the proposed predecessor is already reachable downstream of the successor,
    /// i.e. adding predecessor → successor would close a loop.
    /// </summary>
    private static bool WouldCreateCycle(
        SqliteConnection connection,
        SqliteTransaction tx,
        string predecessor,
        string successor)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { successor };
        var frontier = new Queue<string>();
        frontier.Enqueue(successor);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var next in GatingSuccessorsOf(connection, tx, current))
            {
                if (string.Equals(next, predecessor, StringComparison.Ordinal))
                {
                    return true;
                }

                if (visited.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        return false;
    }

    private static List<string> GatingSuccessorsOf(
        SqliteConnection connection,
        SqliteTransaction tx,
        string taskId)
    {
        var ids = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            SELECT successor_task_id FROM task_dependencies
            WHERE predecessor_task_id = $id AND dependency_type IN ($blocks, $informs);
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.Parameters.AddWithValue("$blocks", TaskDependencyTypes.Blocks);
        cmd.Parameters.AddWithValue("$informs", TaskDependencyTypes.Informs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static TaskDependencyRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        PredecessorTaskId = reader.GetString(1),
        SuccessorTaskId = reader.GetString(2),
        DependencyType = reader.GetString(3),
        Reason = reader.IsDBNull(4) ? null : reader.GetString(4),
        Expects = reader.IsDBNull(5) ? null : reader.GetString(5),
        Confidence = reader.IsDBNull(6) ? null : reader.GetDouble(6),
        EvidenceRef = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedBy = reader.IsDBNull(8) ? CreatedByActors.Agent : reader.GetString(8),
        CreatedAt = reader.GetString(9),
        FollowUpAt = reader.IsDBNull(10) ? null : reader.GetString(10),
        Cadence = reader.IsDBNull(11) ? null : reader.GetString(11),
        SatisfiedAt = reader.IsDBNull(12) ? null : reader.GetString(12),
    };

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string dependencyId,
        string actor,
        object detail,
        string now,
        MutationProvenance? provenance)
    {
        using var audit = connection.CreateCommand();
        audit.Transaction = tx;
        audit.CommandText =
            """
            INSERT INTO audit_events (id, event_type, entity_type, entity_id, actor, detail_json, created_at)
            VALUES ($id, $etype, $ent, $eid, $actor, $detail, $t);
            """;
        audit.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        audit.Parameters.AddWithValue("$etype", eventType);
        audit.Parameters.AddWithValue("$ent", EntityTypes.Task);
        audit.Parameters.AddWithValue("$eid", dependencyId);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance));
        audit.Parameters.AddWithValue("$t", now);
        audit.ExecuteNonQuery();
    }

    private static string NormalizeType(string? dependencyType)
    {
        var type = string.IsNullOrWhiteSpace(dependencyType)
            ? TaskDependencyTypes.Blocks
            : dependencyType.Trim().ToLowerInvariant();
        if (!TaskDependencyTypes.All.Contains(type))
        {
            throw new ArgumentException("Unknown dependency type.", nameof(dependencyType));
        }

        return type;
    }

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? CreatedByActors.Agent : actor.Trim();

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
