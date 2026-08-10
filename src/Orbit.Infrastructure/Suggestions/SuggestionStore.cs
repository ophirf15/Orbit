using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Suggestions;

public sealed class SuggestionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnectionFactory _factory;

    public SuggestionStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<AgentSuggestionRecord> List(string? status = null, int limit = 100)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        var take = Math.Clamp(limit, 1, 500);
        if (string.IsNullOrWhiteSpace(status))
        {
            cmd.CommandText =
                """
                SELECT id, suggestion_type, summary, payload_json, project_id, workstream_id, task_id, note_id,
                       status, confidence, created_at, updated_at
                FROM agent_suggestions
                WHERE archived_at IS NULL
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
        }
        else
        {
            if (!SuggestionStatuses.All.Contains(status))
            {
                throw new ArgumentException("Unknown suggestion status.", nameof(status));
            }

            cmd.CommandText =
                """
                SELECT id, suggestion_type, summary, payload_json, project_id, workstream_id, task_id, note_id,
                       status, confidence, created_at, updated_at
                FROM agent_suggestions
                WHERE archived_at IS NULL AND status = $status
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$status", status);
        }

        cmd.Parameters.AddWithValue("$limit", take);
        return ReadAll(cmd);
    }

    public AgentSuggestionRecord? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, suggestion_type, summary, payload_json, project_id, workstream_id, task_id, note_id,
                   status, confidence, created_at, updated_at
            FROM agent_suggestions
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return ReadAll(cmd).FirstOrDefault();
    }

    public AgentSuggestionRecord Create(CreateSuggestionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SuggestionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Summary);

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO agent_suggestions (
              id, suggestion_type, summary, payload_json, project_id, workstream_id, task_id, note_id,
              status, confidence, created_at, updated_at)
            VALUES (
              $id, $type, $summary, $payload, $project, $ws, $task, $note,
              $status, $confidence, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$type", request.SuggestionType.Trim());
        cmd.Parameters.AddWithValue("$summary", request.Summary.Trim());
        cmd.Parameters.AddWithValue("$payload", (object?)request.PayloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)request.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ws", (object?)request.WorkstreamId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$task", (object?)request.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$note", (object?)request.NoteId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$confidence", (object?)request.Confidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();

        return Get(id) ?? throw new InvalidOperationException("Suggestion was not readable after insert.");
    }

    public bool HasPendingForNote(string noteId, string suggestionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM agent_suggestions
            WHERE note_id = $note
              AND suggestion_type = $type
              AND status = $status
              AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$note", noteId);
        cmd.Parameters.AddWithValue("$type", suggestionType);
        cmd.Parameters.AddWithValue("$status", SuggestionStatuses.Pending);
        return cmd.ExecuteScalar() is not null;
    }

    public bool HasPendingForTask(string taskId, string suggestionType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM agent_suggestions
            WHERE task_id = $task
              AND suggestion_type = $type
              AND status = $status
              AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$task", taskId);
        cmd.Parameters.AddWithValue("$type", suggestionType);
        cmd.Parameters.AddWithValue("$status", SuggestionStatuses.Pending);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Dedupe for suggestions keyed by ids inside <c>payload_json</c> (task pairs, task+email).
    /// Both tokens must appear in the payload of the same pending suggestion.
    /// </summary>
    public bool HasPendingForPayloadTokens(string suggestionType, string tokenA, string tokenB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenA);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenB);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM agent_suggestions
            WHERE suggestion_type = $type
              AND status = $status
              AND archived_at IS NULL
              AND payload_json LIKE $a
              AND payload_json LIKE $b
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$type", suggestionType);
        cmd.Parameters.AddWithValue("$status", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$a", $"%{tokenA}%");
        cmd.Parameters.AddWithValue("$b", $"%{tokenB}%");
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>True when the pair was already proposed and answered, in either direction.</summary>
    public bool WasDecidedForPayloadTokens(string suggestionType, string tokenA, string tokenB)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionType);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM agent_suggestions
            WHERE suggestion_type = $type
              AND status IN ($accepted, $rejected)
              AND payload_json LIKE $a
              AND payload_json LIKE $b
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$type", suggestionType);
        cmd.Parameters.AddWithValue("$accepted", SuggestionStatuses.Accepted);
        cmd.Parameters.AddWithValue("$rejected", SuggestionStatuses.Rejected);
        cmd.Parameters.AddWithValue("$a", $"%{tokenA}%");
        cmd.Parameters.AddWithValue("$b", $"%{tokenB}%");
        return cmd.ExecuteScalar() is not null;
    }

    public SuggestionAcceptResult Accept(
        string id,
        string? actor = null,
        MutationProvenance? provenance = null,
        string? applyProjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var suggestion = Get(id)
            ?? throw new ArgumentException("Suggestion was not found.", nameof(id));
        if (!string.Equals(suggestion.Status, SuggestionStatuses.Pending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Suggestion status is '{suggestion.Status}', expected pending.");
        }

        var actorSeed = provenance?.ResolveActor(actor) ?? actor;
        var requestedBy = string.IsNullOrWhiteSpace(actorSeed) ? CreatedByActors.User : actorSeed.Trim();
        var now = DateTime.UtcNow.ToString("O");
        string? appliedNoteId = null;
        string? appliedProjectId = null;
        string? createdTaskId = null;
        string? appliedTaskId = null;
        string? createdDependencyId = null;

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        switch (suggestion.SuggestionType)
        {
            case SuggestionTypes.AssignToProject:
            case SuggestionTypes.AssignProjectLegacy:
            {
                var (noteId, projectId) = ResolveAssignPayload(suggestion);
                appliedNoteId = noteId;
                appliedProjectId = projectId;
                createdTaskId = ApplyAssignToProject(connection, tx, noteId, projectId, now);
                SetStatus(connection, tx, id, now, projectId);
                break;
            }

            case SuggestionTypes.LinkTasks:
            {
                var link = ResolveLinkPayload(suggestion);
                createdDependencyId = TaskDependencyStore.LinkCore(
                    connection,
                    tx,
                    link.PredecessorTaskId,
                    link.SuccessorTaskId,
                    link.DependencyType,
                    link.Reason ?? suggestion.Summary,
                    link.Expects,
                    suggestion.Confidence,
                    link.EvidenceRef,
                    requestedBy,
                    now);
                appliedTaskId = link.SuccessorTaskId;
                SetStatus(connection, tx, id, now);
                break;
            }

            case SuggestionTypes.MergeIntoTask:
            {
                var merge = ResolveMergePayload(suggestion);
                appliedTaskId = merge.TaskId;
                ApplyMergeIntoTask(connection, tx, merge, now);
                SetStatus(connection, tx, id, now);
                break;
            }

            case SuggestionTypes.ReportingRelationship:
            {
                var report = ResolveReportingPayload(suggestion);
                ApplyReportingRelationship(connection, tx, report, now);
                SetStatus(connection, tx, id, now);
                break;
            }

            case SuggestionTypes.DisambiguateEmailClaim:
            {
                appliedProjectId = ApplyDisambiguateEmailClaim(
                    connection,
                    tx,
                    suggestion,
                    applyProjectId,
                    now);
                SetStatus(connection, tx, id, now, appliedProjectId);
                break;
            }

            default:
                SetStatus(connection, tx, id, now);
                break;
        }

        WriteAudit(
            connection,
            tx,
            "suggestion.accepted",
            id,
            requestedBy,
            new
            {
                suggestionType = suggestion.SuggestionType,
                appliedNoteId,
                appliedProjectId,
                createdTaskId,
                appliedTaskId,
                createdDependencyId,
            },
            now,
            provenance);

        tx.Commit();

        return new SuggestionAcceptResult
        {
            Suggestion = Get(id)!,
            AppliedNoteId = appliedNoteId,
            AppliedProjectId = appliedProjectId,
            CreatedTaskId = createdTaskId,
            AppliedTaskId = appliedTaskId,
            CreatedDependencyId = createdDependencyId,
        };
    }

    private static string ApplyDisambiguateEmailClaim(
        SqliteConnection connection,
        SqliteTransaction tx,
        AgentSuggestionRecord suggestion,
        string? applyProjectId,
        string now)
    {
        string? emailId = null;
        string? projectId = applyProjectId;
        if (!string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            using var doc = JsonDocument.Parse(suggestion.PayloadJson);
            if (doc.RootElement.TryGetProperty("emailId", out var emailEl))
            {
                emailId = emailEl.GetString();
            }

            if (string.IsNullOrWhiteSpace(projectId)
                && doc.RootElement.TryGetProperty("projectId", out var projectEl))
            {
                projectId = projectEl.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            projectId = suggestion.ProjectId;
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException(
                "Disambiguation accept requires applyProjectId (or payload.projectId).",
                nameof(applyProjectId));
        }

        if (string.IsNullOrWhiteSpace(emailId))
        {
            throw new InvalidOperationException("Disambiguation suggestion is missing emailId.");
        }

        using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = tx;
            ensure.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            ensure.Parameters.AddWithValue("$id", projectId);
            if (ensure.ExecuteScalar() is null)
            {
                throw new ArgumentException("Project was not found.", nameof(applyProjectId));
            }
        }

        using (var link = connection.CreateCommand())
        {
            link.Transaction = tx;
            link.CommandText =
                """
                INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at)
                VALUES ($id, $email, $project, $t)
                ON CONFLICT(email_artifact_id, project_id) DO NOTHING;
                """;
            link.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            link.Parameters.AddWithValue("$email", emailId);
            link.Parameters.AddWithValue("$project", projectId);
            link.Parameters.AddWithValue("$t", now);
            link.ExecuteNonQuery();
        }

        return projectId;
    }

    private static void SetStatus(
        SqliteConnection connection,
        SqliteTransaction tx,
        string id,
        string now,
        string? projectId = null)
    {
        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = projectId is null
            ? """
              UPDATE agent_suggestions
              SET status = $status, updated_at = $t
              WHERE id = $id;
              """
            : """
              UPDATE agent_suggestions
              SET status = $status, project_id = $project, updated_at = $t
              WHERE id = $id;
              """;
        upd.Parameters.AddWithValue("$status", SuggestionStatuses.Accepted);
        upd.Parameters.AddWithValue("$t", now);
        upd.Parameters.AddWithValue("$id", id);
        if (projectId is not null)
        {
            upd.Parameters.AddWithValue("$project", projectId);
        }

        upd.ExecuteNonQuery();
    }

    public AgentSuggestionRecord Reject(string id, string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var suggestion = Get(id)
            ?? throw new ArgumentException("Suggestion was not found.", nameof(id));
        if (!string.Equals(suggestion.Status, SuggestionStatuses.Pending, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Suggestion status is '{suggestion.Status}', expected pending.");
        }

        var requestedBy = string.IsNullOrWhiteSpace(actor) ? CreatedByActors.User : actor.Trim();
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                """
                UPDATE agent_suggestions
                SET status = $status, updated_at = $t
                WHERE id = $id;
                """;
            upd.Parameters.AddWithValue("$status", SuggestionStatuses.Rejected);
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", id);
            upd.ExecuteNonQuery();
        }

        WriteAudit(
            connection,
            tx,
            "suggestion.rejected",
            id,
            requestedBy,
            new { suggestionType = suggestion.SuggestionType, noteId = suggestion.NoteId },
            now);

        tx.Commit();
        return Get(id)!;
    }

    /// <summary>
    /// Clears pending "thinking" chores (e.g. review_limbo). Limbo stays on the workbench.
    /// </summary>
    public int DismissThinkingOnlyPending()
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE agent_suggestions
            SET status = $rejected, updated_at = $t
            WHERE status = $pending AND suggestion_type = $type;
            """;
        cmd.Parameters.AddWithValue("$rejected", SuggestionStatuses.Rejected);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$pending", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$type", SuggestionTypes.ReviewLimbo);
        return cmd.ExecuteNonQuery();
    }

    private (string NoteId, string ProjectId) ResolveAssignPayload(AgentSuggestionRecord suggestion)
    {
        string? noteId = suggestion.NoteId;
        string? projectId = suggestion.ProjectId;

        if (!string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            using var doc = JsonDocument.Parse(suggestion.PayloadJson);
            if (doc.RootElement.TryGetProperty("noteId", out var n) && n.ValueKind == JsonValueKind.String)
            {
                noteId ??= n.GetString();
            }

            if (doc.RootElement.TryGetProperty("projectId", out var p) && p.ValueKind == JsonValueKind.String)
            {
                projectId ??= p.GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(noteId))
        {
            throw new InvalidOperationException("assign_to_project suggestion is missing noteId.");
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new InvalidOperationException("assign_to_project suggestion is missing projectId.");
        }

        return (noteId, projectId);
    }

    private sealed record LinkTasksPayload(
        string PredecessorTaskId,
        string SuccessorTaskId,
        string? DependencyType,
        string? Reason,
        string? Expects,
        string? EvidenceRef);

    private sealed record MergeIntoTaskPayload(
        string TaskId,
        string Text,
        string Field,
        string? SourceType,
        string? SourceId);

    private sealed record ReportingPayload(
        string PersonId,
        string ReportsToPersonId,
        string? OrganizationId);

    private static LinkTasksPayload ResolveLinkPayload(AgentSuggestionRecord suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            throw new InvalidOperationException("link_tasks suggestion is missing its payload.");
        }

        using var doc = JsonDocument.Parse(suggestion.PayloadJson);
        var root = doc.RootElement;
        var predecessor = ReadString(root, "predecessorTaskId");
        var successor = ReadString(root, "successorTaskId") ?? suggestion.TaskId;

        if (string.IsNullOrWhiteSpace(predecessor) || string.IsNullOrWhiteSpace(successor))
        {
            throw new InvalidOperationException(
                "link_tasks suggestion needs predecessorTaskId and successorTaskId.");
        }

        return new LinkTasksPayload(
            predecessor,
            successor,
            ReadString(root, "dependencyType"),
            ReadString(root, "reason"),
            ReadString(root, "expects"),
            ReadString(root, "evidenceRef"));
    }

    private static ReportingPayload ResolveReportingPayload(AgentSuggestionRecord suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            throw new InvalidOperationException("reporting_relationship suggestion is missing its payload.");
        }

        using var doc = JsonDocument.Parse(suggestion.PayloadJson);
        var root = doc.RootElement;
        var personId = ReadString(root, "personId");
        var reportsTo = ReadString(root, "reportsToPersonId");
        if (string.IsNullOrWhiteSpace(personId) || string.IsNullOrWhiteSpace(reportsTo))
        {
            throw new InvalidOperationException(
                "reporting_relationship suggestion needs personId and reportsToPersonId.");
        }

        return new ReportingPayload(personId, reportsTo, ReadString(root, "organizationId"));
    }

    private static void ApplyReportingRelationship(
        SqliteConnection connection,
        SqliteTransaction tx,
        ReportingPayload report,
        string now)
    {
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText =
                """
                SELECT id FROM reporting_relationships
                WHERE person_id = $person
                  AND reports_to_person_id = $manager
                  AND archived_at IS NULL
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$person", report.PersonId);
            find.Parameters.AddWithValue("$manager", report.ReportsToPersonId);
            if (find.ExecuteScalar() is not null)
            {
                return;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO reporting_relationships (
              id, person_id, reports_to_person_id, organization_id, created_at, updated_at)
            VALUES ($id, $person, $manager, $org, $t, $t);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$person", report.PersonId);
        insert.Parameters.AddWithValue("$manager", report.ReportsToPersonId);
        insert.Parameters.AddWithValue("$org", (object?)report.OrganizationId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
    }

    private static MergeIntoTaskPayload ResolveMergePayload(AgentSuggestionRecord suggestion)
    {
        if (string.IsNullOrWhiteSpace(suggestion.PayloadJson))
        {
            throw new InvalidOperationException("merge_into_task suggestion is missing its payload.");
        }

        using var doc = JsonDocument.Parse(suggestion.PayloadJson);
        var root = doc.RootElement;
        var taskId = ReadString(root, "taskId") ?? suggestion.TaskId;
        var text = ReadString(root, "text");

        if (string.IsNullOrWhiteSpace(taskId))
        {
            throw new InvalidOperationException("merge_into_task suggestion is missing taskId.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("merge_into_task suggestion is missing text.");
        }

        var field = ReadString(root, "field");
        field = string.Equals(field, "next_action", StringComparison.OrdinalIgnoreCase)
            ? "next_action"
            : "body";

        return new MergeIntoTaskPayload(
            taskId,
            text,
            field,
            ReadString(root, "sourceType"),
            ReadString(root, "sourceId"));
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Appends inbound info to a task. Never overwrites existing notes — the merge is additive
    /// and attributed, so accepting can't silently destroy what the user already wrote.
    /// </summary>
    private static void ApplyMergeIntoTask(
        SqliteConnection connection,
        SqliteTransaction tx,
        MergeIntoTaskPayload merge,
        string now)
    {
        string? currentBody;
        string? currentNext;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText =
                "SELECT body, next_action FROM tasks WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            read.Parameters.AddWithValue("$id", merge.TaskId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentException("Task was not found.", nameof(merge));
            }

            currentBody = reader.IsDBNull(0) ? null : reader.GetString(0);
            currentNext = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var attribution = merge.SourceType switch
        {
            "email" => "From email",
            "note" => "From note",
            null or "" => "From agent",
            _ => $"From {merge.SourceType}",
        };
        var stamped = $"{attribution} ({now[..10]}): {merge.Text.Trim()}";

        var newBody = string.IsNullOrWhiteSpace(currentBody)
            ? stamped
            : $"{currentBody.TrimEnd()}\n\n{stamped}";

        var newNext = string.Equals(merge.Field, "next_action", StringComparison.Ordinal)
            ? merge.Text.Trim()
            : currentNext;

        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            """
            UPDATE tasks
            SET body = $body, next_action = $next, updated_at = $t
            WHERE id = $id;
            """;
        upd.Parameters.AddWithValue("$body", newBody);
        upd.Parameters.AddWithValue("$next", (object?)newNext ?? DBNull.Value);
        upd.Parameters.AddWithValue("$t", now);
        upd.Parameters.AddWithValue("$id", merge.TaskId);
        upd.ExecuteNonQuery();
    }

    private static string? ApplyAssignToProject(
        SqliteConnection connection,
        SqliteTransaction tx,
        string noteId,
        string projectId,
        string now)
    {
        using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = tx;
            ensure.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
            ensure.Parameters.AddWithValue("$id", projectId);
            if (ensure.ExecuteScalar() is null)
            {
                throw new ArgumentException("Project was not found.", nameof(projectId));
            }
        }

        string? originalText;
        using (var note = connection.CreateCommand())
        {
            note.Transaction = tx;
            note.CommandText =
                """
                SELECT original_text FROM notes
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            note.Parameters.AddWithValue("$id", noteId);
            originalText = note.ExecuteScalar() as string;
            if (originalText is null)
            {
                throw new ArgumentException("Note was not found.", nameof(noteId));
            }
        }

        var taskId = Guid.NewGuid().ToString("D");
        using (var taskCmd = connection.CreateCommand())
        {
            taskCmd.Transaction = tx;
            taskCmd.CommandText =
                """
                INSERT INTO tasks (
                  id, project_id, workstream_id, title, body, status, priority,
                  next_action, created_at, updated_at)
                VALUES (
                  $id, $project, NULL, $title, NULL, $status, NULL,
                  NULL, $t, $t);
                """;
            taskCmd.Parameters.AddWithValue("$id", taskId);
            taskCmd.Parameters.AddWithValue("$project", projectId);
            taskCmd.Parameters.AddWithValue("$title", originalText);
            taskCmd.Parameters.AddWithValue("$status", TaskStatuses.NotStarted);
            taskCmd.Parameters.AddWithValue("$t", now);
            taskCmd.ExecuteNonQuery();
        }

        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText =
                """
                UPDATE notes
                SET project_id = $project, task_id = $task, is_limbo = 0, updated_at = $t
                WHERE id = $id;
                """;
            upd.Parameters.AddWithValue("$project", projectId);
            upd.Parameters.AddWithValue("$task", taskId);
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", noteId);
            upd.ExecuteNonQuery();
        }

        // original_text must remain unchanged — never written here.
        return taskId;
    }

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string suggestionId,
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
        audit.Parameters.AddWithValue("$ent", EntityTypes.AgentSuggestion);
        audit.Parameters.AddWithValue("$eid", suggestionId);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance));
        audit.Parameters.AddWithValue("$t", now);
        audit.ExecuteNonQuery();
    }

    private static List<AgentSuggestionRecord> ReadAll(SqliteCommand cmd)
    {
        var list = new List<AgentSuggestionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new AgentSuggestionRecord
            {
                Id = reader.GetString(0),
                SuggestionType = reader.GetString(1),
                Summary = reader.GetString(2),
                PayloadJson = reader.IsDBNull(3) ? null : reader.GetString(3),
                ProjectId = reader.IsDBNull(4) ? null : reader.GetString(4),
                WorkstreamId = reader.IsDBNull(5) ? null : reader.GetString(5),
                TaskId = reader.IsDBNull(6) ? null : reader.GetString(6),
                NoteId = reader.IsDBNull(7) ? null : reader.GetString(7),
                Status = reader.GetString(8),
                Confidence = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                CreatedAt = reader.GetString(10),
                UpdatedAt = reader.GetString(11),
            });
        }

        return list;
    }
}
