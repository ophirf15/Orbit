using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Context;

/// <summary>
/// Bounded evidence pack for a project / workstream / task. Extractions stay project-scoped;
/// shared vendors appear as related entities without merging task contexts.
/// </summary>
public sealed class ContextBundleService
{
    private const int TaskLimit = 40;
    private const int NoteLimit = 30;
    private const int BlockerLimit = 20;
    private const int EmailLimit = 20;
    private const int ContactLimit = 20;
    private const int FileLimit = 40;
    private const int SuggestionLimit = 20;
    private const int RelatedLimit = 30;

    private readonly SqliteConnectionFactory _factory;
    private readonly CalendarReadStore _calendar;

    public ContextBundleService(SqliteConnectionFactory factory, CalendarReadStore? calendar = null)
    {
        _factory = factory;
        _calendar = calendar ?? new CalendarReadStore(factory);
    }

    public ContextBundle? GetBundle(string targetType, string targetId, string? attentionProjectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetType);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);

        if (!ContextTargetTypes.All.Contains(targetType))
        {
            throw new ArgumentException(
                "targetType must be project, workstream, or task.",
                nameof(targetType));
        }

        using var connection = _factory.CreateConnection();
        var resolved = ResolveTarget(connection, targetType, targetId);
        if (resolved is null)
        {
            return null;
        }

        var attention = string.IsNullOrWhiteSpace(attentionProjectId) ? null : attentionProjectId.Trim();
        var aligned = attention is not null
            && string.Equals(attention, resolved.ProjectId, StringComparison.Ordinal);

        var homePath = LoadHomeFolderPath(connection, resolved.ProjectId);
        string? sandboxPath = null;
        if (!string.IsNullOrWhiteSpace(homePath))
        {
            sandboxPath = Path.Combine(homePath, Orbit.Infrastructure.Files.OrbitHomeSandbox.FolderName);
        }

        return new ContextBundle
        {
            TargetType = resolved.TargetType,
            TargetId = resolved.TargetId,
            ProjectId = resolved.ProjectId,
            ProjectName = resolved.ProjectName,
            ProjectSummary = resolved.ProjectSummary,
            WorkstreamId = resolved.WorkstreamId,
            TaskId = resolved.TaskId,
            AttentionProjectId = attention,
            AttentionAligned = aligned,
            HomeFolderPath = homePath,
            OrbitSandboxPath = sandboxPath,
            Tasks = LoadTasks(connection, resolved),
            Blockers = LoadBlockers(connection, resolved),
            Notes = LoadNotes(connection, resolved),
            Emails = LoadEmails(connection, resolved.ProjectId),
            Contacts = LoadContacts(connection, resolved.ProjectId),
            Files = LoadFiles(connection, resolved.ProjectId),
            Meetings = LoadMeetings(resolved.ProjectId),
            Suggestions = LoadSuggestions(connection, resolved.ProjectId),
            RelatedEntities = LoadRelatedEntities(connection, resolved.ProjectId),
        };
    }

    private static string? LoadHomeFolderPath(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT root_path FROM project_folders
            WHERE project_id = $p AND is_home = 1 AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        return cmd.ExecuteScalar() as string;
    }

    private static ResolvedTarget? ResolveTarget(SqliteConnection connection, string targetType, string targetId)
    {
        if (string.Equals(targetType, ContextTargetTypes.Project, StringComparison.OrdinalIgnoreCase))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, name, summary
                FROM projects
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", targetId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ResolvedTarget(
                ContextTargetTypes.Project,
                targetId,
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                WorkstreamId: null,
                TaskId: null);
        }

        if (string.Equals(targetType, ContextTargetTypes.Workstream, StringComparison.OrdinalIgnoreCase))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT w.id, w.project_id, p.name, p.summary
                FROM workstreams w
                INNER JOIN projects p ON p.id = w.project_id
                WHERE w.id = $id AND w.archived_at IS NULL AND p.archived_at IS NULL
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", targetId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ResolvedTarget(
                ContextTargetTypes.Workstream,
                targetId,
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                WorkstreamId: reader.GetString(0),
                TaskId: null);
        }

        // task
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT t.id, t.project_id, t.workstream_id, p.name, p.summary
                FROM tasks t
                INNER JOIN projects p ON p.id = t.project_id
                WHERE t.id = $id AND t.archived_at IS NULL AND p.archived_at IS NULL
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", targetId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ResolvedTarget(
                ContextTargetTypes.Task,
                targetId,
                reader.GetString(1),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                WorkstreamId: reader.IsDBNull(2) ? null : reader.GetString(2),
                TaskId: reader.GetString(0));
        }
    }

    private static IReadOnlyList<ContextBundleTask> LoadTasks(SqliteConnection connection, ResolvedTarget target)
    {
        using var cmd = connection.CreateCommand();
        if (target.TaskId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, title, status, next_action, workstream_id
                FROM tasks
                WHERE id = $tid AND archived_at IS NULL
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$tid", target.TaskId);
        }
        else if (target.WorkstreamId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, title, status, next_action, workstream_id
                FROM tasks
                WHERE workstream_id = $ws AND archived_at IS NULL
                ORDER BY
                  CASE status
                    WHEN $blocked THEN 0
                    WHEN $waiting THEN 1
                    WHEN $active THEN 2
                    WHEN $notStarted THEN 3
                    ELSE 4
                  END,
                  updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$ws", target.WorkstreamId);
            cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
            cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
            cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
            cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);
            cmd.Parameters.AddWithValue("$limit", TaskLimit);
        }
        else
        {
            cmd.CommandText =
                """
                SELECT id, title, status, next_action, workstream_id
                FROM tasks
                WHERE project_id = $p AND archived_at IS NULL
                ORDER BY
                  CASE status
                    WHEN $blocked THEN 0
                    WHEN $waiting THEN 1
                    WHEN $active THEN 2
                    WHEN $notStarted THEN 3
                    ELSE 4
                  END,
                  updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", target.ProjectId);
            cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
            cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
            cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
            cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);
            cmd.Parameters.AddWithValue("$limit", TaskLimit);
        }

        var list = new List<ContextBundleTask>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleTask
            {
                TaskId = reader.GetString(0),
                Title = reader.GetString(1),
                Status = reader.GetString(2),
                NextAction = reader.IsDBNull(3) ? null : reader.GetString(3),
                WorkstreamId = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleBlocker> LoadBlockers(SqliteConnection connection, ResolvedTarget target)
    {
        using var cmd = connection.CreateCommand();
        if (target.TaskId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, summary, status, task_id
                FROM blockers
                WHERE task_id = $tid AND archived_at IS NULL
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$tid", target.TaskId);
        }
        else if (target.WorkstreamId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, summary, status, task_id
                FROM blockers
                WHERE workstream_id = $ws AND archived_at IS NULL
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$ws", target.WorkstreamId);
        }
        else
        {
            cmd.CommandText =
                """
                SELECT id, summary, status, task_id
                FROM blockers
                WHERE project_id = $p AND archived_at IS NULL
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", target.ProjectId);
        }

        cmd.Parameters.AddWithValue("$limit", BlockerLimit);
        var list = new List<ContextBundleBlocker>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleBlocker
            {
                Id = reader.GetString(0),
                Summary = reader.GetString(1),
                Status = reader.GetString(2),
                TaskId = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleNote> LoadNotes(SqliteConnection connection, ResolvedTarget target)
    {
        using var cmd = connection.CreateCommand();
        if (target.TaskId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, original_text, created_at
                FROM notes
                WHERE task_id = $tid AND archived_at IS NULL
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$tid", target.TaskId);
        }
        else if (target.WorkstreamId is not null)
        {
            cmd.CommandText =
                """
                SELECT id, original_text, created_at
                FROM notes
                WHERE workstream_id = $ws AND archived_at IS NULL
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$ws", target.WorkstreamId);
        }
        else
        {
            cmd.CommandText =
                """
                SELECT id, original_text, created_at
                FROM notes
                WHERE project_id = $p AND archived_at IS NULL
                ORDER BY created_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$p", target.ProjectId);
        }

        cmd.Parameters.AddWithValue("$limit", NoteLimit);
        var list = new List<ContextBundleNote>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleNote
            {
                Id = reader.GetString(0),
                OriginalText = reader.GetString(1),
                CreatedAt = reader.GetString(2),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleEmail> LoadEmails(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.id, e.subject, e.sent_at, e.body_preview
            FROM email_artifacts e
            INNER JOIN email_project_links l ON l.email_artifact_id = e.id
            WHERE l.project_id = $p AND e.archived_at IS NULL
            ORDER BY COALESCE(e.sent_at, e.created_at) DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$limit", EmailLimit);

        var emails = new List<(string Id, string? Subject, string? SentAt, string? Preview)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                emails.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        var list = new List<ContextBundleEmail>(emails.Count);
        foreach (var email in emails)
        {
            list.Add(new ContextBundleEmail
            {
                Id = email.Id,
                Subject = email.Subject,
                SentAt = email.SentAt,
                BodyPreview = email.Preview,
                Extractions = LoadExtractionsForProject(connection, email.Id, projectId),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleExtraction> LoadExtractionsForProject(
        SqliteConnection connection,
        string emailId,
        string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, extraction_type, summary, project_id, workstream_id, confidence
            FROM email_extractions
            WHERE email_artifact_id = $email
              AND project_id = $p
              AND archived_at IS NULL
            ORDER BY created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$p", projectId);

        var list = new List<ContextBundleExtraction>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleExtraction
            {
                Id = reader.GetString(0),
                ExtractionType = reader.GetString(1),
                Summary = reader.GetString(2),
                ProjectId = reader.GetString(3),
                WorkstreamId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Confidence = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleContact> LoadContacts(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT p.id, p.display_name
            FROM relationships r
            INNER JOIN people p ON p.id = r.source_id
            WHERE r.source_type = $person
              AND r.target_type = $project
              AND r.target_id = $pid
              AND r.project_id = $pid
              AND p.archived_at IS NULL
            ORDER BY p.display_name COLLATE NOCASE
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$person", EntityTypes.Person);
        cmd.Parameters.AddWithValue("$project", EntityTypes.Project);
        cmd.Parameters.AddWithValue("$pid", projectId);
        cmd.Parameters.AddWithValue("$limit", ContactLimit);

        var list = new List<ContextBundleContact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleContact
            {
                PersonId = reader.GetString(0),
                DisplayName = reader.GetString(1),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleFile> LoadFiles(SqliteConnection connection, string projectId)
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
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$limit", FileLimit);

        var list = new List<ContextBundleFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleFile
            {
                Id = reader.GetString(0),
                DisplayName = reader.GetString(1),
                Path = reader.GetString(2),
                Extension = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private IReadOnlyList<ContextBundleMeeting> LoadMeetings(string projectId)
    {
        return _calendar.GetMeetingsForProject(projectId, limit: 20)
            .Select(m => new ContextBundleMeeting
            {
                Id = m.Id,
                Title = m.Title,
                StartsAt = m.StartsAt,
                EndsAt = m.EndsAt,
                Location = m.Location,
                AttentionScore = m.AttentionScore,
                SourceName = m.SourceName,
                MailboxName = m.MailboxName,
                CalendarName = m.CalendarName,
            })
            .ToList();
    }

    private static IReadOnlyList<ContextBundleSuggestion> LoadSuggestions(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, summary, status, suggestion_type, note_id, confidence
            FROM agent_suggestions
            WHERE project_id = $p
              AND status = $pending
              AND archived_at IS NULL
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$pending", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$limit", SuggestionLimit);

        var list = new List<ContextBundleSuggestion>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ContextBundleSuggestion
            {
                Id = reader.GetString(0),
                Summary = reader.GetString(1),
                Status = reader.GetString(2),
                SuggestionType = reader.IsDBNull(3) ? null : reader.GetString(3),
                NoteId = reader.IsDBNull(4) ? null : reader.GetString(4),
                Confidence = reader.IsDBNull(5) ? null : reader.GetDouble(5),
            });
        }

        return list;
    }

    private static IReadOnlyList<ContextBundleRelatedEntity> LoadRelatedEntities(
        SqliteConnection connection,
        string projectId)
    {
        var list = new List<ContextBundleRelatedEntity>();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT o.id, o.name, r.relationship_type
                FROM relationships r
                INNER JOIN organizations o ON o.id = r.source_id
                WHERE r.source_type = $org
                  AND r.target_type = $project
                  AND r.target_id = $pid
                  AND r.project_id = $pid
                  AND o.archived_at IS NULL
                ORDER BY o.name COLLATE NOCASE
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$org", EntityTypes.Organization);
            cmd.Parameters.AddWithValue("$project", EntityTypes.Project);
            cmd.Parameters.AddWithValue("$pid", projectId);
            cmd.Parameters.AddWithValue("$limit", RelatedLimit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ContextBundleRelatedEntity
                {
                    EntityType = EntityTypes.Organization,
                    EntityId = reader.GetString(0),
                    Label = reader.GetString(1),
                    RelationshipType = reader.GetString(2),
                });
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT p.id, p.display_name, r.relationship_type
                FROM relationships r
                INNER JOIN people p ON p.id = r.source_id
                WHERE r.source_type = $person
                  AND r.target_type = $project
                  AND r.target_id = $pid
                  AND r.project_id = $pid
                  AND p.archived_at IS NULL
                ORDER BY p.display_name COLLATE NOCASE
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$person", EntityTypes.Person);
            cmd.Parameters.AddWithValue("$project", EntityTypes.Project);
            cmd.Parameters.AddWithValue("$pid", projectId);
            cmd.Parameters.AddWithValue("$limit", RelatedLimit);
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && list.Count < RelatedLimit)
            {
                list.Add(new ContextBundleRelatedEntity
                {
                    EntityType = EntityTypes.Person,
                    EntityId = reader.GetString(0),
                    Label = reader.GetString(1),
                    RelationshipType = reader.GetString(2),
                });
            }
        }

        return list;
    }

    private sealed record ResolvedTarget(
        string TargetType,
        string TargetId,
        string ProjectId,
        string ProjectName,
        string? ProjectSummary,
        string? WorkstreamId,
        string? TaskId);
}
