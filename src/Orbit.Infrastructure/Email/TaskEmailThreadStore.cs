using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Email;

public sealed class TaskEmailThreadRecord
{
    public required string Id { get; init; }

    public required string TaskId { get; init; }

    public required string ConversationId { get; init; }

    public string? AnchorEmailId { get; init; }

    public required string LinkedBy { get; init; }

    public required string CreatedAt { get; init; }

    public string? Subject { get; init; }

    public string? LatestSentAt { get; init; }

    public int MessageCount { get; init; }
}

/// <summary>Durable task ↔ Outlook conversation links (not Hermes chat conversations).</summary>
public sealed class TaskEmailThreadStore
{
    private readonly SqliteConnectionFactory _factory;

    public TaskEmailThreadStore(SqliteConnectionFactory factory) => _factory = factory;

    public TaskEmailThreadRecord Link(
        string taskId,
        string conversationId,
        string? anchorEmailId = null,
        string? actor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var task = taskId.Trim();
        var conversation = conversationId.Trim();
        var linkedBy = string.IsNullOrWhiteSpace(actor) ? CreatedByActors.User : actor.Trim();
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        EnsureTask(connection, task);

        using (var existing = connection.CreateCommand())
        {
            existing.CommandText =
                """
                SELECT id FROM task_email_threads
                WHERE task_id = $task AND conversation_id = $conv AND archived_at IS NULL
                LIMIT 1;
                """;
            existing.Parameters.AddWithValue("$task", task);
            existing.Parameters.AddWithValue("$conv", conversation);
            if (existing.ExecuteScalar() is string existingId)
            {
                return Get(existingId) ?? throw new InvalidOperationException("Thread link unreadable.");
            }
        }

        var id = Guid.NewGuid().ToString("D");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                INSERT INTO task_email_threads (
                  id, task_id, conversation_id, anchor_email_id, linked_by, created_at, updated_at)
                VALUES ($id, $task, $conv, $anchor, $by, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$task", task);
            cmd.Parameters.AddWithValue("$conv", conversation);
            cmd.Parameters.AddWithValue("$anchor", (object?)NullIfWhite(anchorEmailId) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$by", linkedBy);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        return Get(id) ?? throw new InvalidOperationException("Thread link unreadable after insert.");
    }

    public bool Unlink(string threadLinkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadLinkId);
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE task_email_threads
            SET archived_at = $t, updated_at = $t
            WHERE id = $id AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$id", threadLinkId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public TaskEmailThreadRecord? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.task_id, t.conversation_id, t.anchor_email_id, t.linked_by, t.created_at,
                   (
                     SELECT e.subject FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                     ORDER BY COALESCE(e.sent_at, e.received_at, e.created_at) DESC
                     LIMIT 1
                   ) AS subject,
                   (
                     SELECT COALESCE(e.sent_at, e.received_at, e.created_at) FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                     ORDER BY COALESCE(e.sent_at, e.received_at, e.created_at) DESC
                     LIMIT 1
                   ) AS latest_sent_at,
                   (
                     SELECT COUNT(*) FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                   ) AS message_count
            FROM task_email_threads t
            WHERE t.id = $id AND t.archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<TaskEmailThreadRecord> ListForTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.task_id, t.conversation_id, t.anchor_email_id, t.linked_by, t.created_at,
                   (
                     SELECT e.subject FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                     ORDER BY COALESCE(e.sent_at, e.received_at, e.created_at) DESC
                     LIMIT 1
                   ) AS subject,
                   (
                     SELECT COALESCE(e.sent_at, e.received_at, e.created_at) FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                     ORDER BY COALESCE(e.sent_at, e.received_at, e.created_at) DESC
                     LIMIT 1
                   ) AS latest_sent_at,
                   (
                     SELECT COUNT(*) FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                   ) AS message_count
            FROM task_email_threads t
            WHERE t.task_id = $task AND t.archived_at IS NULL
            ORDER BY t.created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$task", taskId.Trim());
        var list = new List<TaskEmailThreadRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }

        return list;
    }

    /// <summary>Resolves the stored .msg path for shell-open in Outlook.</summary>
    public string? GetEmailRawPath(string emailId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT raw_path FROM email_artifacts
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", emailId.Trim());
        return cmd.ExecuteScalar() as string;
    }

    private static TaskEmailThreadRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        TaskId = reader.GetString(1),
        ConversationId = reader.GetString(2),
        AnchorEmailId = reader.IsDBNull(3) ? null : reader.GetString(3),
        LinkedBy = reader.GetString(4),
        CreatedAt = reader.GetString(5),
        Subject = reader.IsDBNull(6) ? null : reader.GetString(6),
        LatestSentAt = reader.IsDBNull(7) ? null : reader.GetString(7),
        MessageCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
    };

    private static void EnsureTask(SqliteConnection connection, string taskId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM tasks WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", taskId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Task was not found.", nameof(taskId));
        }
    }

    private static string? NullIfWhite(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
