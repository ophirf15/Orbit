using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Core.Workbench;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Changes;

/// <summary>
/// Assembles task History facts from change log + existing graph relations (no new schema).
/// </summary>
public sealed class TaskHistoryReadStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ChangeLogStore _changes;

    public TaskHistoryReadStore(SqliteConnectionFactory factory, ChangeLogStore changes)
    {
        _factory = factory;
        _changes = changes;
    }

    public IReadOnlyList<TaskTimelineFact> ListFacts(string taskId, int limit = 120)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var id = taskId.Trim();
        var facts = new List<TaskTimelineFact>();

        using var connection = _factory.CreateConnection();
        if (!TryAddCreated(connection, id, facts))
        {
            return [];
        }

        AddAuditFacts(connection, id, facts);
        AddChangeLogFacts(id, facts);
        AddNoteFacts(connection, id, facts);
        AddBlockerFacts(connection, id, facts);
        AddWaitingOnFacts(connection, id, facts);
        AddEmailFacts(connection, id, facts);
        AddFileFacts(connection, id, facts);

        // Do not Take() here — order is insertion order, not time. Mapper sorts + caps.
        return facts;
    }

    private static bool TryAddCreated(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT created_at
            FROM tasks
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        var created = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(created))
        {
            return false;
        }

        facts.Add(new TaskTimelineFact
        {
            Kind = TaskTimelineKinds.Created,
            At = created,
            DedupeKey = $"created|{taskId}",
        });
        return true;
    }

    private static void AddAuditFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT event_type, detail_json, created_at
            FROM audit_events
            WHERE entity_type = $etype AND entity_id = $id
            ORDER BY created_at DESC
            LIMIT 80;
            """;
        cmd.Parameters.AddWithValue("$etype", EntityTypes.Task);
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var eventType = reader.GetString(0);
            var detailJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            var at = reader.GetString(2);

            if (string.Equals(eventType, "task.created", StringComparison.OrdinalIgnoreCase))
            {
                // Created already from tasks.created_at.
                continue;
            }

            if (string.Equals(eventType, "task.updated", StringComparison.OrdinalIgnoreCase)
                || string.Equals(eventType, "task.moved", StringComparison.OrdinalIgnoreCase))
            {
                AddAuditUpdateFacts(at, detailJson, facts);
                continue;
            }

            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Change,
                At = at,
                SourceEvent = eventType,
                Summary = SummarizeDetail(detailJson),
                DedupeKey = $"audit|{eventType}|{at}",
            });
        }
    }

    private static void AddAuditUpdateFacts(string at, string? detailJson, List<TaskTimelineFact> facts)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Change,
                At = at,
                SourceEvent = "task.updated",
                DedupeKey = $"audit|task.updated|{at}",
            });
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            var added = false;

            if (TryGetString(root, "status", out var status) && !string.IsNullOrWhiteSpace(status))
            {
                facts.Add(new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.Status,
                    At = at,
                    StatusLabel = StatusLabel(status),
                    Summary = status,
                    DedupeKey = $"status|{at}|{status}",
                });
                added = true;
            }

            if (root.TryGetProperty("body", out _))
            {
                facts.Add(new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.BriefUpdate,
                    At = at,
                    Summary = "living brief",
                    DedupeKey = $"brief|{at}",
                });
                added = true;
            }

            if (TryGetString(root, "nextAction", out var next) && !string.IsNullOrWhiteSpace(next))
            {
                facts.Add(new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.Change,
                    At = at,
                    SourceEvent = "task.updated",
                    Summary = $"next: {Truncate(next, 40)}",
                    DedupeKey = $"next|{at}",
                });
                added = true;
            }

            if (!added)
            {
                facts.Add(new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.Change,
                    At = at,
                    SourceEvent = "task.updated",
                    Summary = SummarizeDetail(detailJson),
                    DedupeKey = $"audit|task.updated|{at}",
                });
            }
        }
        catch (JsonException)
        {
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Change,
                At = at,
                SourceEvent = "task.updated",
                DedupeKey = $"audit|task.updated|{at}",
            });
        }
    }

    private void AddChangeLogFacts(string taskId, List<TaskTimelineFact> facts)
    {
        foreach (var entry in _changes.ListForEntity("task", taskId, limit: 60))
        {
            var source = entry.SourceEvent ?? entry.ChangeKind;
            // Prefer audit for ordinary task.updated (has field detail). Keep briefing / pulse / note signals.
            if (string.Equals(source, "task.updated", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var kind = string.Equals(source, "operator.briefing", StringComparison.OrdinalIgnoreCase)
                ? TaskTimelineKinds.BriefUpdate
                : TaskTimelineKinds.Change;

            facts.Add(new TaskTimelineFact
            {
                Kind = kind,
                At = entry.CreatedAt,
                SourceEvent = source,
                Summary = kind == TaskTimelineKinds.BriefUpdate ? "Hermes" : null,
                DedupeKey = $"changelog|{entry.Revision}",
            });
        }
    }

    private static void AddNoteFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, original_text, created_at
            FROM notes
            WHERE task_id = $id AND archived_at IS NULL
            ORDER BY created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var noteId = reader.GetString(0);
            var text = reader.GetString(1);
            var at = reader.GetString(2);
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Note,
                At = at,
                Summary = Truncate(text, 64),
                DedupeKey = $"note|{noteId}",
            });
        }
    }

    private static void AddBlockerFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, summary, status, created_at, updated_at, archived_at
            FROM blockers
            WHERE task_id = $id
            ORDER BY created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blockerId = reader.GetString(0);
            var summary = reader.GetString(1);
            var status = reader.GetString(2);
            var createdAt = reader.GetString(3);
            var updatedAt = reader.IsDBNull(4) ? createdAt : reader.GetString(4);
            var archivedAt = reader.IsDBNull(5) ? null : reader.GetString(5);

            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.BlockerSet,
                At = createdAt,
                Summary = Truncate(summary, 64),
                DedupeKey = $"blocker-set|{blockerId}",
            });

            var cleared = !string.IsNullOrWhiteSpace(archivedAt)
                || !string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            if (cleared)
            {
                facts.Add(new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.BlockerCleared,
                    At = string.IsNullOrWhiteSpace(archivedAt) ? updatedAt : archivedAt!,
                    Summary = Truncate(summary, 64),
                    DedupeKey = $"blocker-clear|{blockerId}",
                });
            }
        }
    }

    private static void AddWaitingOnFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT d.id, d.created_at, d.expects, t.title
            FROM task_dependencies d
            JOIN tasks t ON t.id = d.predecessor_task_id
            WHERE d.successor_task_id = $id
            ORDER BY d.created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var depId = reader.GetString(0);
            var at = reader.GetString(1);
            var expects = reader.IsDBNull(2) ? null : reader.GetString(2);
            var title = reader.GetString(3);
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.WaitingOnLinked,
                At = at,
                Summary = Truncate(title, 56),
                Detail = string.IsNullOrWhiteSpace(expects) ? null : Truncate(expects, 40),
                DedupeKey = $"waiting|{depId}",
            });
        }
    }

    private static void AddEmailFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.created_at,
                   (
                     SELECT e.subject FROM email_artifacts e
                     WHERE e.conversation_id = t.conversation_id AND e.archived_at IS NULL
                     ORDER BY COALESCE(e.sent_at, e.received_at, e.created_at) DESC
                     LIMIT 1
                   ) AS subject
            FROM task_email_threads t
            WHERE t.task_id = $id AND t.archived_at IS NULL
            ORDER BY t.created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var threadId = reader.GetString(0);
            var at = reader.GetString(1);
            var subject = reader.IsDBNull(2) ? null : reader.GetString(2);
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.EmailLinked,
                At = at,
                Summary = string.IsNullOrWhiteSpace(subject) ? null : Truncate(subject, 64),
                DedupeKey = $"email|{threadId}",
            });
        }
    }

    private static void AddFileFacts(SqliteConnection connection, string taskId, List<TaskTimelineFact> facts)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT fel.id, fel.created_at, COALESCE(fa.display_name, fa.path, fa.id)
            FROM file_entity_links fel
            JOIN file_artifacts fa ON fa.id = fel.file_artifact_id
            WHERE fel.entity_type = 'task' AND fel.entity_id = $id
            ORDER BY fel.created_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var linkId = reader.GetString(0);
            var at = reader.GetString(1);
            var name = reader.GetString(2);
            facts.Add(new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.FileLinked,
                At = at,
                Summary = Truncate(name, 64),
                DedupeKey = $"file|{linkId}",
            });
        }
    }

    private static string StatusLabel(string status) => status.Trim().ToLowerInvariant() switch
    {
        "not_started" => "New",
        "active" => "Active",
        "waiting" => "Waiting",
        "blocked" => "Blocked",
        "complete" => "Complete",
        _ => status.Trim(),
    };

    private static string? SummarizeDetail(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            if (TryGetString(root, "summary", out var summary))
            {
                return Truncate(summary, 48);
            }

            if (TryGetString(root, "title", out var title))
            {
                return Truncate(title, 48);
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? string.Empty;
            return true;
        }

        if (el.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = el.ToString();
            return true;
        }

        return false;
    }

    private static string Truncate(string text, int max)
    {
        var t = string.Join(
            ' ',
            (text ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (t.Length <= max)
        {
            return t;
        }

        return t[..(max - 1)].TrimEnd() + "…";
    }
}
