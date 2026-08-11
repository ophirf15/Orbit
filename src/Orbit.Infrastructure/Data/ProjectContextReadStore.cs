using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

public sealed class ProjectContextReadStore
{
    private readonly SqliteConnectionFactory _factory;

    public ProjectContextReadStore(SqliteConnectionFactory factory) => _factory = factory;

    public ProjectContextRecord? GetContext(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using (var projectCmd = connection.CreateCommand())
        {
            projectCmd.CommandText =
                """
                SELECT id, name, summary, code, dossier_json
                FROM projects
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            projectCmd.Parameters.AddWithValue("$id", projectId);
            using var reader = projectCmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var summary = reader.IsDBNull(2) ? null : reader.GetString(2);
            var code = reader.IsDBNull(3) ? null : reader.GetString(3);
            var dossier = ProjectDossier.Parse(reader.IsDBNull(4) ? null : reader.GetString(4));
            reader.Close();

            return new ProjectContextRecord
            {
                Id = id,
                Name = name,
                Summary = summary,
                Code = code,
                Dossier = dossier.IsStructurallyEmpty ? null : dossier,
                DossierEmpty = dossier.IsStructurallyEmpty,
                Aliases = LoadAliases(connection, projectId),
                Tasks = LoadTasks(connection, projectId),
                CompletedTasks = LoadCompletedTasks(connection, projectId),
                Notes = LoadNotes(connection, projectId),
                Blockers = LoadBlockers(connection, projectId),
                Contacts = LoadContacts(connection, projectId),
                Meetings = LoadMeetings(connection, projectId),
                Suggestions = LoadSuggestions(connection, projectId),
                Files = LoadFiles(connection, projectId),
            };
        }
    }

    public CellLineRecord? GetTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, status, next_action, body, project_id, due_at, priority, urgency,
                   source_kind, source_confidence, source_match_reason
            FROM tasks
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", taskId.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CellLineRecord
        {
            TaskId = reader.GetString(0),
            Title = reader.GetString(1),
            Status = reader.GetString(2),
            NextAction = reader.IsDBNull(3) ? null : reader.GetString(3),
            Body = reader.IsDBNull(4) ? null : reader.GetString(4),
            ProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
            DueAt = reader.IsDBNull(6) ? null : reader.GetString(6),
            Priority = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            Urgency = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            SourceKind = reader.IsDBNull(9) ? null : reader.GetString(9),
            SourceConfidence = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            SourceMatchReason = reader.IsDBNull(11) ? null : reader.GetString(11),
        };
    }

    public LimboNoteRecord? GetLimboNote(string noteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(noteId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT n.id, n.original_text, n.created_at,
                   (SELECT s.id FROM agent_suggestions s
                    WHERE s.note_id = n.id AND s.status = 'pending'
                    ORDER BY s.created_at DESC LIMIT 1) AS suggestion_id,
                   (SELECT s.summary FROM agent_suggestions s
                    WHERE s.note_id = n.id AND s.status = 'pending'
                    ORDER BY s.created_at DESC LIMIT 1) AS suggestion_summary
            FROM notes n
            WHERE n.id = $id AND n.is_limbo = 1 AND n.archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", noteId.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LimboNoteRecord
        {
            Id = reader.GetString(0),
            OriginalText = reader.GetString(1),
            CreatedAt = reader.GetString(2),
            SuggestionId = reader.IsDBNull(3) ? null : reader.GetString(3),
            SuggestionSummary = reader.IsDBNull(4) ? null : reader.GetString(4),
        };
    }

    private static IReadOnlyList<ProjectAliasItem> LoadAliases(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, alias
            FROM project_aliases
            WHERE project_id = $p
            ORDER BY alias COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        var list = new List<ProjectAliasItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ProjectAliasItem
            {
                Id = reader.GetString(0),
                Alias = reader.GetString(1),
            });
        }

        return list;
    }

    private static IReadOnlyList<CellLineRecord> LoadTasks(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, status, next_action, body, due_at, priority, urgency
            FROM tasks
            WHERE project_id = $p
              AND archived_at IS NULL
              AND status IN ($blocked, $waiting, $active, $notStarted)
            ORDER BY
              CASE status
                WHEN $blocked THEN 0
                WHEN $waiting THEN 1
                WHEN $active THEN 2
                WHEN $notStarted THEN 3
                ELSE 4
              END,
              updated_at DESC
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
        cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
        cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);

        var list = new List<CellLineRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadTaskLine(reader));
        }

        return list;
    }

    private static IReadOnlyList<CellLineRecord> LoadCompletedTasks(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, status, next_action, body, due_at, priority, urgency
            FROM tasks
            WHERE project_id = $p
              AND archived_at IS NULL
              AND status = $complete
            ORDER BY updated_at DESC
            LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);

        var list = new List<CellLineRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadTaskLine(reader));
        }

        return list;
    }

    private static CellLineRecord ReadTaskLine(SqliteDataReader reader) => new()
    {
        TaskId = reader.GetString(0),
        Title = reader.GetString(1),
        Status = reader.GetString(2),
        NextAction = reader.IsDBNull(3) ? null : reader.GetString(3),
        Body = reader.IsDBNull(4) ? null : reader.GetString(4),
        DueAt = reader.IsDBNull(5) ? null : reader.GetString(5),
        Priority = reader.IsDBNull(6) ? null : reader.GetInt32(6),
        Urgency = reader.IsDBNull(7) ? null : reader.GetInt32(7),
    };

    private static IReadOnlyList<ContextNoteRecord> LoadNotes(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, original_text, created_at
            FROM notes
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY created_at DESC
            LIMIT 30;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextNoteRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextNoteRecord
            {
                Id = reader.GetString(0),
                OriginalText = reader.GetString(1),
                CreatedAt = reader.GetString(2),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBlockerRecord> LoadBlockers(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, summary, status, task_id
            FROM blockers
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY updated_at DESC
            LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextBlockerRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBlockerRecord
            {
                Id = reader.GetString(0),
                Summary = reader.GetString(1),
                Status = reader.GetString(2),
                TaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextContactRecord> LoadContacts(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT p.id, p.display_name,
                   (SELECT m.title FROM organization_memberships m
                    WHERE m.person_id = p.id AND m.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1) AS title,
                   (SELECT o.name FROM organization_memberships m
                    INNER JOIN organizations o ON o.id = m.organization_id
                    WHERE m.person_id = p.id AND m.archived_at IS NULL AND o.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1) AS org_name
            FROM relationships r
            INNER JOIN people p ON p.id = r.source_id
            WHERE r.source_type = $person
              AND r.target_type = $project
              AND r.target_id = $pid
              AND r.project_id = $pid
              AND p.archived_at IS NULL
            ORDER BY p.display_name COLLATE NOCASE
            LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$person", EntityTypes.Person);
        cmd.Parameters.AddWithValue("$project", EntityTypes.Project);
        cmd.Parameters.AddWithValue("$pid", projectId);

        var list = new List<ContextContactRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextContactRecord
            {
                PersonId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                OrganizationName = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextMeetingRecord> LoadMeetings(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.id, e.title, e.starts_at
            FROM calendar_events e
            INNER JOIN event_entity_links l
              ON l.calendar_event_id = e.id
             AND l.entity_type = 'project'
             AND l.entity_id = $p
            WHERE e.archived_at IS NULL
            ORDER BY COALESCE(e.starts_at, e.created_at) DESC
            LIMIT 10;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextMeetingRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextMeetingRecord
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                StartsAt = reader.IsDBNull(2) ? null : reader.GetString(2),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextSuggestionRecord> LoadSuggestions(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, summary, status, note_id
            FROM agent_suggestions
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY created_at DESC
            LIMIT 20;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextSuggestionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextSuggestionRecord
            {
                Id = reader.GetString(0),
                Summary = reader.GetString(1),
                Status = reader.GetString(2),
                NoteId = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextFileRecord> LoadFiles(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT fa.id, COALESCE(fa.display_name, fa.path), fa.path, fa.extension
            FROM file_artifacts fa
            INNER JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
            WHERE fa.archived_at IS NULL
              AND fpl.project_id = $p
            ORDER BY fa.display_name COLLATE NOCASE
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextFileRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextFileRecord
            {
                Id = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Path = reader.GetString(2),
                Extension = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }
}
