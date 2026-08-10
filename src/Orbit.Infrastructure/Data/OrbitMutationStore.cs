using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Typed Orbit-owned mutations for Hermes tools and Host APIs. Never touches external FS/SQL/shell.
/// </summary>
public sealed class OrbitMutationStore
{
    private readonly SqliteConnectionFactory _factory;

    public OrbitMutationStore(SqliteConnectionFactory factory) => _factory = factory;

    public MutationTaskResult CreateTask(
        string title,
        string projectId,
        string? status,
        string? actor,
        MutationProvenance? provenance = null,
        string? nextAction = null,
        string? body = null,
        string? workstreamId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var trimmed = title.Trim();
        var taskStatus = string.IsNullOrWhiteSpace(status) ? TaskStatuses.NotStarted : status.Trim();
        if (!TaskStatuses.All.Contains(taskStatus))
        {
            throw new ArgumentException("Unknown task status.", nameof(status));
        }

        EnsureProject(projectId);
        string? wsId = null;
        if (!string.IsNullOrWhiteSpace(workstreamId))
        {
            wsId = workstreamId.Trim();
            EnsureWorkstream(projectId, wsId);
        }

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var next = string.IsNullOrWhiteSpace(nextAction) ? null : nextAction.Trim();
        var brief = string.IsNullOrWhiteSpace(body) ? null : body.Trim();

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO tasks (
                  id, project_id, workstream_id, title, body, status, priority,
                  next_action, created_at, updated_at)
                VALUES (
                  $id, $project, $ws, $title, $body, $status, NULL,
                  $next, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$ws", (object?)wsId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", trimmed);
            cmd.Parameters.AddWithValue("$body", (object?)brief ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$status", taskStatus);
            cmd.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "task.created",
            EntityTypes.Task,
            id,
            requestedBy,
            new { title = trimmed, projectId, workstreamId = wsId, status = taskStatus, nextAction = next, body = brief },
            now,
            provenance);
        tx.Commit();

        return new MutationTaskResult
        {
            Id = id,
            Title = trimmed,
            ProjectId = projectId,
            Status = taskStatus,
            NextAction = next,
            Body = brief,
            WorkstreamId = wsId,
        };
    }

    public MutationWorkstreamResult CreateWorkstream(
        string projectId,
        string name,
        string? nextAction = null,
        string? actor = null,
        MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > 200)
        {
            throw new ArgumentException("Workstream name is too long.", nameof(name));
        }

        EnsureProject(projectId);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        var next = string.IsNullOrWhiteSpace(nextAction) ? null : nextAction.Trim();
        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO workstreams (
                  id, project_id, name, status, priority, next_action, created_at, updated_at)
                VALUES (
                  $id, $project, $name, 'active', NULL, $next, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$name", trimmed);
            cmd.Parameters.AddWithValue("$next", (object?)next ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "workstream.created",
            EntityTypes.Workstream,
            id,
            requestedBy,
            new { projectId, name = trimmed, nextAction = next },
            now,
            provenance);
        tx.Commit();

        return new MutationWorkstreamResult
        {
            Id = id,
            ProjectId = projectId,
            Name = trimmed,
            Status = "active",
            NextAction = next,
        };
    }

    public IReadOnlyList<MutationWorkstreamResult> ListWorkstreams(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        EnsureProject(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, name, status, next_action
            FROM workstreams
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$p", projectId.Trim());
        var list = new List<MutationWorkstreamResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new MutationWorkstreamResult
            {
                Id = reader.GetString(0),
                ProjectId = reader.GetString(1),
                Name = reader.GetString(2),
                Status = reader.GetString(3),
                NextAction = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return list;
    }

    private void EnsureWorkstream(string projectId, string workstreamId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM workstreams
            WHERE id = $id AND project_id = $p AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", workstreamId);
        cmd.Parameters.AddWithValue("$p", projectId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Workstream was not found on that project.", nameof(workstreamId));
        }
    }

    public MutationTaskResult UpdateTask(
        string taskId,
        string? title,
        string? status,
        string? nextAction,
        string? actor,
        MutationProvenance? provenance = null,
        string? body = null,
        string? dueAt = null,
        int? priority = null,
        int? urgency = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (title is null && status is null && nextAction is null && body is null && dueAt is null
            && priority is null && urgency is null)
        {
            throw new ArgumentException(
                "At least one of title, status, nextAction, body, dueAt, priority, or urgency is required.");
        }

        if (status is not null && !TaskStatuses.All.Contains(status.Trim()))
        {
            throw new ArgumentException("Unknown task status.", nameof(status));
        }

        if (priority is not null && priority is not (0 or 1))
        {
            throw new ArgumentException("priority must be 0 (less important) or 1 (important).", nameof(priority));
        }

        if (urgency is not null && urgency is not (0 or 1))
        {
            throw new ArgumentException("urgency must be 0 (less urgent) or 1 (urgent).", nameof(urgency));
        }

        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");
        var applied = new Dictionary<string, string>(StringComparer.Ordinal);

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT project_id, title, status, next_action, body, due_at, priority, urgency
                FROM tasks
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$id", taskId);
            using var reader = find.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentException("Task was not found.", nameof(taskId));
            }

            var projectId = reader.GetString(0);
            var currentTitle = reader.GetString(1);
            var currentStatus = reader.GetString(2);
            var currentNext = reader.IsDBNull(3) ? null : reader.GetString(3);
            var currentBody = reader.IsDBNull(4) ? null : reader.GetString(4);
            var currentDue = reader.IsDBNull(5) ? null : reader.GetString(5);
            var currentPriority = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var currentUrgency = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            reader.Close();

            var newTitle = string.IsNullOrWhiteSpace(title) ? currentTitle : title.Trim();
            var newStatus = string.IsNullOrWhiteSpace(status) ? currentStatus : status.Trim();
            var newNext = nextAction is null ? currentNext : nextAction.Trim();
            var newBody = body is null ? currentBody : body;
            var newDue = dueAt is null ? currentDue : (string.IsNullOrWhiteSpace(dueAt) ? null : dueAt.Trim());
            var newPriority = priority ?? currentPriority;
            var newUrgency = urgency ?? currentUrgency;

            using var upd = connection.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText =
                """
                UPDATE tasks
                SET title = $title,
                    status = $status,
                    next_action = $next,
                    body = $body,
                    due_at = $due,
                    priority = $priority,
                    urgency = $urgency,
                    updated_at = $t
                WHERE id = $id;
                """;
            upd.Parameters.AddWithValue("$title", newTitle);
            upd.Parameters.AddWithValue("$status", newStatus);
            upd.Parameters.AddWithValue("$next", (object?)newNext ?? DBNull.Value);
            upd.Parameters.AddWithValue("$body", (object?)newBody ?? DBNull.Value);
            upd.Parameters.AddWithValue("$due", (object?)newDue ?? DBNull.Value);
            upd.Parameters.AddWithValue("$priority", (object?)newPriority ?? DBNull.Value);
            upd.Parameters.AddWithValue("$urgency", (object?)newUrgency ?? DBNull.Value);
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", taskId);
            upd.ExecuteNonQuery();

            if (!string.Equals(currentTitle, newTitle, StringComparison.Ordinal))
            {
                applied["title"] = newTitle;
            }

            if (!string.Equals(currentStatus, newStatus, StringComparison.Ordinal))
            {
                applied["status"] = newStatus;
            }

            if (!string.Equals(currentNext, newNext, StringComparison.Ordinal))
            {
                applied["nextAction"] = newNext ?? string.Empty;
            }

            if (!string.Equals(currentBody, newBody, StringComparison.Ordinal))
            {
                applied["body"] = newBody ?? string.Empty;
            }

            if (!string.Equals(currentDue, newDue, StringComparison.Ordinal))
            {
                applied["dueAt"] = newDue ?? string.Empty;
            }

            if (currentPriority != newPriority)
            {
                applied["priority"] = newPriority?.ToString() ?? string.Empty;
            }

            if (currentUrgency != newUrgency)
            {
                applied["urgency"] = newUrgency?.ToString() ?? string.Empty;
            }

            WriteAudit(
                connection,
                tx,
                "task.updated",
                EntityTypes.Task,
                taskId,
                requestedBy,
                applied,
                now,
                provenance);
            tx.Commit();

            return new MutationTaskResult
            {
                Id = taskId,
                Title = newTitle,
                ProjectId = projectId,
                Status = newStatus,
                NextAction = newNext,
                Body = newBody,
                DueAt = newDue,
                Priority = newPriority,
                Urgency = newUrgency,
            };
        }
    }

    public ArchiveResult Archive(string entityType, string entityId, string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        var type = entityType.Trim().ToLowerInvariant();
        var table = type switch
        {
            "project" or EntityTypes.Project => ("projects", EntityTypes.Project, "project.archived"),
            "task" or EntityTypes.Task => ("tasks", EntityTypes.Task, "task.archived"),
            "note" or EntityTypes.Note => ("notes", EntityTypes.Note, "note.archived"),
            _ => throw new ArgumentException($"Unsupported archive entity type '{entityType}'.", nameof(entityType)),
        };

        var now = DateTime.UtcNow.ToString("O");
        var requestedBy = NormalizeActor(actor);

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = $"SELECT 1 FROM {table.Item1} WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            find.Parameters.AddWithValue("$id", entityId.Trim());
            if (find.ExecuteScalar() is null)
            {
                throw new ArgumentException($"{table.Item2} was not found or is already archived.", nameof(entityId));
            }
        }

        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                $"""
                UPDATE {table.Item1}
                SET archived_at = $t, updated_at = $t
                WHERE id = $id AND archived_at IS NULL;
                """;
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", entityId.Trim());
            if (upd.ExecuteNonQuery() == 0)
            {
                throw new ArgumentException($"{table.Item2} was not found or is already archived.", nameof(entityId));
            }
        }

        // Soft-archive child tasks/notes when archiving a project so workbench stays clean.
        if (table.Item2 == EntityTypes.Project)
        {
            using var childTasks = connection.CreateCommand();
            childTasks.Transaction = tx;
            childTasks.CommandText =
                """
                UPDATE tasks SET archived_at = $t, updated_at = $t
                WHERE project_id = $p AND archived_at IS NULL;
                """;
            childTasks.Parameters.AddWithValue("$t", now);
            childTasks.Parameters.AddWithValue("$p", entityId.Trim());
            childTasks.ExecuteNonQuery();

            using var childNotes = connection.CreateCommand();
            childNotes.Transaction = tx;
            childNotes.CommandText =
                """
                UPDATE notes SET archived_at = $t, updated_at = $t
                WHERE project_id = $p AND archived_at IS NULL;
                """;
            childNotes.Parameters.AddWithValue("$t", now);
            childNotes.Parameters.AddWithValue("$p", entityId.Trim());
            childNotes.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            table.Item3,
            table.Item2,
            entityId.Trim(),
            requestedBy,
            new { entityType = table.Item2 },
            now);
        tx.Commit();

        return new ArchiveResult
        {
            EntityType = table.Item2,
            EntityId = entityId.Trim(),
            ArchivedAt = now,
        };
    }

    public MutationLinkResult LinkEntities(
        string sourceType,
        string sourceId,
        string targetType,
        string targetId,
        string relationshipType,
        string? projectId,
        string? actor,
        MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(relationshipType);

        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO relationships (
                  id, source_type, source_id, target_type, target_id, relationship_type,
                  project_id, workstream_id, task_id, evidence_ref, confidence, confirmed_by_user,
                  created_by, created_at, updated_at)
                VALUES (
                  $id, $st, $sid, $tt, $tid, $rtype,
                  $project, NULL, NULL, NULL, NULL, 1,
                  $by, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$st", sourceType.Trim());
            cmd.Parameters.AddWithValue("$sid", sourceId.Trim());
            cmd.Parameters.AddWithValue("$tt", targetType.Trim());
            cmd.Parameters.AddWithValue("$tid", targetId.Trim());
            cmd.Parameters.AddWithValue("$rtype", relationshipType.Trim());
            cmd.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", requestedBy);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "entities.linked",
            "relationship",
            id,
            requestedBy,
            new { sourceType, sourceId, targetType, targetId, relationshipType, projectId },
            now,
            provenance);
        tx.Commit();

        return new MutationLinkResult { Id = id, RelationshipType = relationshipType.Trim() };
    }

    public MutationBlockerResult SetBlocker(
        string summary,
        string? projectId,
        string? taskId,
        string? actor,
        MutationProvenance? provenance = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);
        if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("projectId or taskId is required.");
        }

        var requestedBy = NormalizeActor(provenance?.ResolveActor(actor) ?? actor);
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        var resolvedProject = projectId;

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        if (!string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(resolvedProject))
        {
            using var find = connection.CreateCommand();
            find.Transaction = tx;
            find.CommandText = "SELECT project_id FROM tasks WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            find.Parameters.AddWithValue("$id", taskId);
            resolvedProject = find.ExecuteScalar() as string
                ?? throw new ArgumentException("Task was not found.", nameof(taskId));
        }

        if (!string.IsNullOrWhiteSpace(resolvedProject))
        {
            EnsureProject(resolvedProject);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                """
                INSERT INTO blockers (id, project_id, workstream_id, task_id, summary, status, created_at, updated_at)
                VALUES ($id, $project, NULL, $task, $summary, 'open', $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$project", (object?)resolvedProject ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$summary", summary.Trim());
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "blocker.created",
            EntityTypes.Blocker,
            id,
            requestedBy,
            new { summary = summary.Trim(), projectId = resolvedProject, taskId },
            now,
            provenance);
        tx.Commit();

        return new MutationBlockerResult
        {
            Id = id,
            Summary = summary.Trim(),
            ProjectId = resolvedProject,
            TaskId = taskId,
        };
    }

    private void EnsureProject(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }

    private static string NormalizeActor(string? actor) =>
        string.IsNullOrWhiteSpace(actor) ? CreatedByActors.Agent : actor.Trim();

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string entityType,
        string entityId,
        string actor,
        object detail,
        string now,
        MutationProvenance? provenance = null)
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
        audit.Parameters.AddWithValue("$ent", entityType);
        audit.Parameters.AddWithValue("$eid", entityId);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance));
        audit.Parameters.AddWithValue("$t", now);
        audit.ExecuteNonQuery();
    }
}

public sealed class MutationTaskResult
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string ProjectId { get; init; }

    public required string Status { get; init; }

    public string? NextAction { get; init; }

    public string? Body { get; init; }

    public string? WorkstreamId { get; init; }

    public string? DueAt { get; init; }

    public int? Priority { get; init; }

    public int? Urgency { get; init; }
}

public sealed class MutationWorkstreamResult
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public required string Name { get; init; }

    public required string Status { get; init; }

    public string? NextAction { get; init; }
}

public sealed class MutationLinkResult
{
    public required string Id { get; init; }

    public required string RelationshipType { get; init; }
}

public sealed class MutationBlockerResult
{
    public required string Id { get; init; }

    public required string Summary { get; init; }

    public string? ProjectId { get; init; }

    public string? TaskId { get; init; }
}

public sealed class ArchiveResult
{
    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string ArchivedAt { get; init; }
}
