using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Sync;

namespace Orbit.Infrastructure.Data;

public sealed class NoteWriteStore
{
    public const int MaxCaptureLength = 4000;

    private readonly SqliteConnectionFactory _factory;
    private readonly SyncLineageStore? _lineage;

    public NoteWriteStore(SqliteConnectionFactory factory, SyncLineageStore? lineage = null)
    {
        _factory = factory;
        _lineage = lineage;
    }

    public CaptureResult CreateCapture(string text, string? projectId)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Capture text is required.", nameof(text));
        }

        if (trimmed.Length > MaxCaptureLength)
        {
            throw new ArgumentException($"Capture text exceeds {MaxCaptureLength} characters.", nameof(text));
        }

        using var connection = _factory.CreateConnection();

        if (!string.IsNullOrWhiteSpace(projectId))
        {
            EnsureProjectExists(connection, projectId);
        }

        var now = DateTime.UtcNow.ToString("O");
        var noteId = Guid.NewGuid().ToString("D");
        string? taskId = null;
        var isLimbo = string.IsNullOrWhiteSpace(projectId);

        using var tx = connection.BeginTransaction();

        if (!isLimbo)
        {
            taskId = Guid.NewGuid().ToString("D");
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
                taskCmd.Parameters.AddWithValue("$project", projectId!);
                taskCmd.Parameters.AddWithValue("$title", trimmed);
                taskCmd.Parameters.AddWithValue("$status", TaskStatuses.NotStarted);
                taskCmd.Parameters.AddWithValue("$t", now);
                taskCmd.ExecuteNonQuery();
            }
        }

        using (var noteCmd = connection.CreateCommand())
        {
            noteCmd.Transaction = tx;
            noteCmd.CommandText =
                """
                INSERT INTO notes (
                  id, project_id, workstream_id, task_id, original_text, is_limbo, created_at, updated_at)
                VALUES (
                  $id, $project, NULL, $task, $text, $limbo, $t, $t);
                """;
            noteCmd.Parameters.AddWithValue("$id", noteId);
            noteCmd.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
            noteCmd.Parameters.AddWithValue("$task", (object?)taskId ?? DBNull.Value);
            noteCmd.Parameters.AddWithValue("$text", trimmed);
            noteCmd.Parameters.AddWithValue("$limbo", isLimbo ? 1 : 0);
            noteCmd.Parameters.AddWithValue("$t", now);
            noteCmd.ExecuteNonQuery();
        }

        tx.Commit();

        IndexCapture(noteId, taskId, projectId, trimmed, now);
        try
        {
            _lineage?.MarkDirty();
        }
        catch (Exception)
        {
            // Sync metadata must never block note capture.
        }

        return new CaptureResult
        {
            NoteId = noteId,
            TaskId = taskId,
            OriginalText = trimmed,
            ProjectId = projectId,
            IsLimbo = isLimbo,
            CreatedAt = now,
        };
    }

    public string UpdateText(string noteId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException("Note text is required.", nameof(text));
        }

        if (trimmed.Length > MaxCaptureLength)
        {
            throw new ArgumentException($"Note text exceeds {MaxCaptureLength} characters.", nameof(text));
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE notes
            SET original_text = $text,
                updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$id", noteId.Trim());
        cmd.Parameters.AddWithValue("$text", trimmed);
        cmd.Parameters.AddWithValue("$t", now);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new ArgumentException("Note was not found.", nameof(noteId));
        }

        try
        {
            using var search = connection.CreateCommand();
            search.CommandText =
                """
                UPDATE search_documents
                SET title = $title,
                    body = $body,
                    updated_at = $t
                WHERE entity_type = 'note' AND entity_id = $id;
                """;
            search.Parameters.AddWithValue("$id", noteId.Trim());
            search.Parameters.AddWithValue("$title", trimmed.Length <= 80 ? trimmed : trimmed[..80]);
            search.Parameters.AddWithValue("$body", trimmed);
            search.Parameters.AddWithValue("$t", now);
            search.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Search projection is best-effort.
        }

        try
        {
            _lineage?.MarkDirty();
        }
        catch (Exception)
        {
            // ignore
        }

        return trimmed;
    }

    /// <summary>
    /// Promotes a limbo note onto a project by creating a task and clearing the limbo flag.
    /// </summary>
    public CaptureResult AssignLimboToProject(string noteId, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        EnsureProjectExists(connection, projectId.Trim());

        using var tx = connection.BeginTransaction();

        string? originalText;
        int isLimbo;
        using (var note = connection.CreateCommand())
        {
            note.Transaction = tx;
            note.CommandText =
                """
                SELECT original_text, is_limbo
                FROM notes
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            note.Parameters.AddWithValue("$id", noteId.Trim());
            using var reader = note.ExecuteReader();
            if (!reader.Read())
            {
                throw new ArgumentException("Note was not found.", nameof(noteId));
            }

            originalText = reader.GetString(0);
            isLimbo = reader.GetInt32(1);
        }

        if (isLimbo != 1)
        {
            throw new ArgumentException("Note is not in limbo.", paramName: null);
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
            taskCmd.Parameters.AddWithValue("$project", projectId.Trim());
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
            upd.Parameters.AddWithValue("$project", projectId.Trim());
            upd.Parameters.AddWithValue("$task", taskId);
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", noteId.Trim());
            upd.ExecuteNonQuery();
        }

        tx.Commit();

        IndexCapture(noteId.Trim(), taskId, projectId.Trim(), originalText, now);
        try
        {
            _lineage?.MarkDirty();
        }
        catch (Exception)
        {
            // Sync metadata must never block assign.
        }

        return new CaptureResult
        {
            NoteId = noteId.Trim(),
            TaskId = taskId,
            OriginalText = originalText,
            ProjectId = projectId.Trim(),
            IsLimbo = false,
            CreatedAt = now,
        };
    }

    private void IndexCapture(string noteId, string? taskId, string? projectId, string text, string now)
    {
        try
        {
            using var connection = _factory.CreateConnection();
            using var tx = connection.BeginTransaction();

            using (var noteDoc = connection.CreateCommand())
            {
                noteDoc.Transaction = tx;
                noteDoc.CommandText =
                    """
                    INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
                    VALUES ($id, 'note', $entity, $project, $title, $body, $t);
                    """;
                noteDoc.Parameters.AddWithValue("$id", noteId);
                noteDoc.Parameters.AddWithValue("$entity", noteId);
                noteDoc.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
                noteDoc.Parameters.AddWithValue("$title", text.Length <= 80 ? text : text[..80]);
                noteDoc.Parameters.AddWithValue("$body", text);
                noteDoc.Parameters.AddWithValue("$t", now);
                noteDoc.ExecuteNonQuery();
            }

            if (taskId is not null)
            {
                using var taskDoc = connection.CreateCommand();
                taskDoc.Transaction = tx;
                taskDoc.CommandText =
                    """
                    INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
                    VALUES ($id, 'task', $entity, $project, $title, '', $t);
                    """;
                taskDoc.Parameters.AddWithValue("$id", taskId);
                taskDoc.Parameters.AddWithValue("$entity", taskId);
                taskDoc.Parameters.AddWithValue("$project", projectId!);
                taskDoc.Parameters.AddWithValue("$title", text);
                taskDoc.Parameters.AddWithValue("$t", now);
                taskDoc.ExecuteNonQuery();
            }

            try
            {
                using var fts = connection.CreateCommand();
                fts.Transaction = tx;
                fts.CommandText = "INSERT INTO search_documents_fts(search_documents_fts) VALUES('rebuild');";
                fts.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
                // Projection table remains authoritative if FTS is unavailable.
            }

            tx.Commit();
        }
        catch (SqliteException)
        {
            // Capture itself already committed; search can be rebuilt later.
        }
    }

    private static void EnsureProjectExists(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }
}
