using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Infrastructure.Data;

public sealed class WorkbenchReadStore
{
    public const int MaxLinesPerCell = 8;

    private readonly SqliteConnectionFactory _factory;

    public WorkbenchReadStore(SqliteConnectionFactory factory) => _factory = factory;

    public WorkbenchSnapshot GetSnapshot(string? projectId = null)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            return GetProjectBoardSnapshot(projectId.Trim());
        }

        using var connection = _factory.CreateConnection();
        var projects = LoadProjects(connection);
        var linesByProject = LoadOpenTaskLines(connection);
        var blockers = LoadBlockerSummaries(connection);
        var meetings = LoadUpcomingMeetings(connection);
        var suggestions = LoadPendingSuggestionCounts(connection);
        var activity = LoadRecentActivity(connection);
        var limboNotes = LoadLimbo(connection);
        var limboLayout = LoadSyntheticLayout(connection, WorkbenchCellKinds.LimboEntityId);

        var cells = new List<ProjectCellRecord>(projects.Count + 1)
        {
            BuildLimboCell(limboNotes, limboLayout),
        };
        foreach (var project in projects)
        {
            linesByProject.TryGetValue(project.Id, out var lines);
            blockers.TryGetValue(project.Id, out var blocker);
            meetings.TryGetValue(project.Id, out var meeting);
            suggestions.TryGetValue(project.Id, out var suggestionCount);
            activity.TryGetValue(project.Id, out var recent);

            cells.Add(new ProjectCellRecord
            {
                Id = project.Id,
                Name = project.Name,
                Code = project.Code,
                Summary = project.Summary,
                Status = project.Status,
                CellKind = WorkbenchCellKinds.Project,
                Lines = lines ?? Array.Empty<CellLineRecord>(),
                OpenBlockerCount = blocker.Count,
                TopBlockerSummary = blocker.TopSummary,
                UpcomingMeetingTitle = meeting.Title,
                UpcomingMeetingStartsAt = meeting.StartsAt,
                PendingSuggestionCount = suggestionCount,
                RecentActivityAt = recent,
                AccentColor = project.AccentColor,
                SortOrder = project.SortOrder,
                BoardX = project.BoardX,
                BoardY = project.BoardY,
                BoardW = project.BoardW,
                BoardH = project.BoardH,
                DossierEmpty = project.DossierEmpty,
                MissingNextAction = HasMissingNextAction(lines),
            });
        }

        return new WorkbenchSnapshot
        {
            Cells = cells,
            Limbo = limboNotes,
            Scope = null,
        };
    }

    private static ProjectCellRecord BuildLimboCell(
        IReadOnlyList<LimboNoteRecord> limboNotes,
        SyntheticLayoutRecord? layout)
    {
        var lines = limboNotes
            .Take(MaxLinesPerCell)
            .Select(n => new CellLineRecord
            {
                TaskId = n.Id,
                Title = n.OriginalText,
                Status = string.Empty,
                NextAction = n.SuggestionSummary,
                Body = n.SuggestionId,
            })
            .ToList();

        return new ProjectCellRecord
        {
            Id = WorkbenchCellKinds.LimboEntityId,
            Name = "Limbo",
            Code = null,
            Summary = limboNotes.Count == 0
                ? "Unassigned captures"
                : $"{limboNotes.Count} unassigned",
            Status = "active",
            CellKind = WorkbenchCellKinds.Limbo,
            Lines = lines,
            OpenBlockerCount = 0,
            TopBlockerSummary = null,
            UpcomingMeetingTitle = null,
            UpcomingMeetingStartsAt = null,
            PendingSuggestionCount = limboNotes.Count(n => !string.IsNullOrWhiteSpace(n.SuggestionId)),
            RecentActivityAt = limboNotes.Count > 0 ? limboNotes[0].CreatedAt : null,
            AccentColor = null,
            SortOrder = layout?.SortOrder ?? 10_000,
            BoardX = layout?.BoardX,
            BoardY = layout?.BoardY,
            BoardW = layout?.BoardW ?? 280,
            BoardH = layout?.BoardH ?? 240,
        };
    }

    private static SyntheticLayoutRecord? LoadSyntheticLayout(SqliteConnection connection, string cellId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT board_x, board_y, board_w, board_h, sort_order
            FROM workbench_synthetic_layouts
            WHERE cell_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", cellId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SyntheticLayoutRecord(
            reader.IsDBNull(0) ? null : reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetDouble(1),
            reader.IsDBNull(2) ? null : reader.GetDouble(2),
            reader.IsDBNull(3) ? null : reader.GetDouble(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4));
    }

    private readonly record struct SyntheticLayoutRecord(
        double? BoardX,
        double? BoardY,
        double? BoardW,
        double? BoardH,
        int SortOrder);

    /// <summary>
    /// Project-scoped board: cells are open tasks that are board-level;
    /// tasks captured as <c>relates</c> lines under another open task stay lines-only (not sibling cells).
    /// Lines remain related tasks via dependencies.
    /// </summary>
    private WorkbenchSnapshot GetProjectBoardSnapshot(string projectId)
    {
        using var connection = _factory.CreateConnection();
        var project = LoadProject(connection, projectId)
            ?? throw new ArgumentException("Project was not found.", nameof(projectId));

        var tasks = LoadOpenTasksForProject(connection, projectId);
        var lineOnlyTaskIds = LoadRelateLineSuccessorIds(connection, projectId);
        var relatedByTask = LoadRelatedTaskLines(connection, projectId);
        var suggestions = LoadPendingSuggestionCountsForTasks(connection, projectId);

        var cells = new List<ProjectCellRecord>(tasks.Count);
        foreach (var task in tasks)
        {
            if (lineOnlyTaskIds.Contains(task.Id))
            {
                continue;
            }

            relatedByTask.TryGetValue(task.Id, out var lines);
            suggestions.TryGetValue(task.Id, out var suggestionCount);
            cells.Add(new ProjectCellRecord
            {
                Id = task.Id,
                Name = task.Title,
                Code = null,
                Summary = task.NextAction,
                Status = task.Status,
                CellKind = WorkbenchCellKinds.Task,
                Lines = lines ?? Array.Empty<CellLineRecord>(),
                OpenBlockerCount = 0,
                TopBlockerSummary = null,
                UpcomingMeetingTitle = null,
                UpcomingMeetingStartsAt = null,
                PendingSuggestionCount = suggestionCount,
                RecentActivityAt = task.UpdatedAt,
                AccentColor = project.AccentColor,
                SortOrder = task.SortOrder,
                BoardX = task.BoardX,
                BoardY = task.BoardY,
                BoardW = task.BoardW,
                BoardH = task.BoardH,
            });
        }

        return new WorkbenchSnapshot
        {
            Cells = cells,
            Limbo = [],
            Scope = new WorkbenchScopeRecord
            {
                Kind = "project",
                ProjectId = project.Id,
                ProjectName = project.Name,
            },
        };
    }

    /// <summary>
    /// Successors of open-board <see cref="TaskDependencyTypes.Relates"/> edges are line captures, not cells.
    /// </summary>
    private static HashSet<string> LoadRelateLineSuccessorIds(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT DISTINCT d.successor_task_id
            FROM task_dependencies d
            INNER JOIN tasks predecessor ON predecessor.id = d.predecessor_task_id
            INNER JOIN tasks successor ON successor.id = d.successor_task_id
            WHERE d.dependency_type = $relates
              AND successor.project_id = $project
              AND predecessor.project_id = $project
              AND predecessor.archived_at IS NULL
              AND successor.archived_at IS NULL
              AND predecessor.status IN ($blocked, $waiting, $active, $notStarted);
            """;
        cmd.Parameters.AddWithValue("$relates", TaskDependencyTypes.Relates);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
        cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
        cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static ProjectRecord? LoadProject(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code, summary, status, accent_color, sort_order, board_x, board_y, board_w, board_h, dossier_json
            FROM projects
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", projectId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var dossier = ProjectDossier.Parse(reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null);
        return new ProjectRecord
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Code = reader.IsDBNull(2) ? null : reader.GetString(2),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Status = reader.GetString(4),
            AccentColor = reader.IsDBNull(5) ? null : reader.GetString(5),
            SortOrder = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            BoardX = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            BoardY = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            BoardW = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            BoardH = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            Dossier = dossier.IsStructurallyEmpty ? null : dossier,
            DossierEmpty = dossier.IsStructurallyEmpty,
        };
    }

    private static List<(string Id, string Title, string Status, string? NextAction, string? UpdatedAt, int SortOrder, double? BoardX, double? BoardY, double? BoardW, double? BoardH)> LoadOpenTasksForProject(
        SqliteConnection connection,
        string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, title, status, next_action, updated_at, sort_order, board_x, board_y, board_w, board_h
            FROM tasks
            WHERE project_id = $project
              AND archived_at IS NULL
              AND status IN ($blocked, $waiting, $active, $notStarted)
            ORDER BY sort_order ASC, updated_at DESC,
              CASE status
                WHEN $blocked THEN 0
                WHEN $waiting THEN 1
                WHEN $active THEN 2
                ELSE 3
              END,
              COALESCE(priority, 999);
            """;
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
        cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
        cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);

        var list = new List<(string, string, string, string?, string?, int, double?, double?, double?, double?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetDouble(9)));
        }

        return list;
    }

    /// <summary>Related open tasks (either direction of a dependency), capped per cell.</summary>
    private static Dictionary<string, IReadOnlyList<CellLineRecord>> LoadRelatedTaskLines(
        SqliteConnection connection,
        string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT anchor.id AS anchor_id,
                   other.id AS other_id,
                   other.title,
                   other.status,
                   other.next_action,
                   d.expects,
                   CASE WHEN d.successor_task_id = anchor.id THEN 0 ELSE 1 END AS waiting_first
            FROM tasks anchor
            INNER JOIN task_dependencies d
              ON d.predecessor_task_id = anchor.id OR d.successor_task_id = anchor.id
            INNER JOIN tasks other
              ON other.id = CASE WHEN d.predecessor_task_id = anchor.id
                                 THEN d.successor_task_id
                                 ELSE d.predecessor_task_id END
            WHERE anchor.project_id = $project
              AND anchor.archived_at IS NULL
              AND other.archived_at IS NULL
              AND other.status IN ($blocked, $waiting, $active, $notStarted)
            ORDER BY waiting_first, other.updated_at DESC;
            """;
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
        cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
        cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);

        var buckets = new Dictionary<string, List<CellLineRecord>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var anchorId = reader.GetString(0);
            var otherId = reader.GetString(1);
            var key = $"{anchorId}:{otherId}";
            if (!seen.Add(key))
            {
                continue;
            }

            if (!buckets.TryGetValue(anchorId, out var list))
            {
                list = [];
                buckets[anchorId] = list;
            }

            if (list.Count >= MaxLinesPerCell)
            {
                continue;
            }

            var expects = reader.IsDBNull(5) ? null : reader.GetString(5);
            var waitingFirst = reader.GetInt64(6) == 0;
            var next = expects is not null
                ? (waitingFirst ? $"needs {expects}" : $"provides {expects}")
                : (reader.IsDBNull(4) ? null : reader.GetString(4));

            list.Add(new CellLineRecord
            {
                TaskId = otherId,
                Title = reader.GetString(2),
                Status = reader.GetString(3),
                NextAction = next,
            });
        }

        return buckets.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<CellLineRecord>)kv.Value,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, int> LoadPendingSuggestionCountsForTasks(
        SqliteConnection connection,
        string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT task_id, COUNT(*)
            FROM agent_suggestions
            WHERE project_id = $project
              AND task_id IS NOT NULL
              AND status = $pending
              AND archived_at IS NULL
              AND suggestion_type <> $reviewLimbo
              AND confidence IS NOT NULL
              AND confidence >= $minConf
            GROUP BY task_id;
            """;
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$pending", SuggestionStatuses.Pending);
        cmd.Parameters.AddWithValue("$reviewLimbo", SuggestionTypes.ReviewLimbo);
        cmd.Parameters.AddWithValue("$minConf", SuggestionHygiene.ActionableMinConfidence);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }

        return map;
    }

    private static List<ProjectRecord> LoadProjects(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code, summary, status, accent_color, sort_order, board_x, board_y, board_w, board_h, dossier_json
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY sort_order ASC, name COLLATE NOCASE;
            """;

        var list = new List<ProjectRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dossier = ProjectDossier.Parse(reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null);
            list.Add(new ProjectRecord
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Code = reader.IsDBNull(2) ? null : reader.GetString(2),
                Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
                Status = reader.GetString(4),
                AccentColor = reader.IsDBNull(5) ? null : reader.GetString(5),
                SortOrder = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                BoardX = reader.IsDBNull(7) ? null : reader.GetDouble(7),
                BoardY = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                BoardW = reader.IsDBNull(9) ? null : reader.GetDouble(9),
                BoardH = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                Dossier = dossier.IsStructurallyEmpty ? null : dossier,
                DossierEmpty = dossier.IsStructurallyEmpty,
            });
        }

        return list;
    }

    private static bool HasMissingNextAction(IReadOnlyList<CellLineRecord>? lines) =>
        lines is { Count: > 0 } && lines.Any(l => string.IsNullOrWhiteSpace(l.NextAction));

    private static Dictionary<string, IReadOnlyList<CellLineRecord>> LoadOpenTaskLines(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, title, status, next_action
            FROM tasks
            WHERE archived_at IS NULL
              AND status IN ($blocked, $waiting, $active, $notStarted)
            ORDER BY updated_at DESC,
              CASE status
                WHEN $blocked THEN 0
                WHEN $waiting THEN 1
                WHEN $active THEN 2
                ELSE 3
              END,
              COALESCE(priority, 999);
            """;
        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
        cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
        cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);

        var buckets = new Dictionary<string, List<CellLineRecord>>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var projectId = reader.GetString(1);
            if (!buckets.TryGetValue(projectId, out var list))
            {
                list = [];
                buckets[projectId] = list;
            }

            if (list.Count >= MaxLinesPerCell)
            {
                continue;
            }

            list.Add(new CellLineRecord
            {
                TaskId = reader.GetString(0),
                Title = reader.GetString(2),
                Status = reader.GetString(3),
                NextAction = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return buckets.ToDictionary(
            static kv => kv.Key,
            static kv => (IReadOnlyList<CellLineRecord>)kv.Value,
            StringComparer.Ordinal);
    }

    private static Dictionary<string, (int Count, string? TopSummary)> LoadBlockerSummaries(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, summary
            FROM blockers
            WHERE archived_at IS NULL
              AND status = 'open'
              AND project_id IS NOT NULL
            ORDER BY updated_at DESC;
            """;

        var map = new Dictionary<string, (int Count, string? TopSummary)>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var projectId = reader.GetString(0);
            var summary = reader.GetString(1);
            if (map.TryGetValue(projectId, out var existing))
            {
                map[projectId] = (existing.Count + 1, existing.TopSummary);
            }
            else
            {
                map[projectId] = (1, summary);
            }
        }

        return map;
    }

    private static Dictionary<string, (string? Title, string? StartsAt)> LoadUpcomingMeetings(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT e.title, e.starts_at, l.entity_id
            FROM calendar_events e
            INNER JOIN event_entity_links l
              ON l.calendar_event_id = e.id
             AND l.entity_type = 'project'
            WHERE e.archived_at IS NULL
              AND e.starts_at IS NOT NULL
              AND e.starts_at >= $now
            ORDER BY e.starts_at ASC;
            """;
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));

        var map = new Dictionary<string, (string? Title, string? StartsAt)>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var projectId = reader.GetString(2);
            if (map.ContainsKey(projectId))
            {
                continue;
            }

            map[projectId] = (
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        return map;
    }

    private static Dictionary<string, int> LoadPendingSuggestionCounts(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, COUNT(*)
            FROM agent_suggestions
            WHERE archived_at IS NULL
              AND status = 'pending'
              AND project_id IS NOT NULL
              AND suggestion_type <> $reviewLimbo
              AND confidence IS NOT NULL
              AND confidence >= $minConf
            GROUP BY project_id;
            """;
        cmd.Parameters.AddWithValue("$reviewLimbo", SuggestionTypes.ReviewLimbo);
        cmd.Parameters.AddWithValue("$minConf", SuggestionHygiene.ActionableMinConfidence);

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }

        return map;
    }

    private static Dictionary<string, string?> LoadRecentActivity(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, MAX(updated_at)
            FROM (
              SELECT project_id, updated_at FROM tasks WHERE project_id IS NOT NULL AND archived_at IS NULL
              UNION ALL
              SELECT project_id, updated_at FROM notes WHERE project_id IS NOT NULL AND archived_at IS NULL
              UNION ALL
              SELECT id AS project_id, updated_at FROM projects WHERE archived_at IS NULL
            )
            GROUP BY project_id;
            """;

        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return map;
    }

    private static List<LimboNoteRecord> LoadLimbo(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT n.id, n.original_text, n.created_at,
                   (
                     SELECT s.id
                     FROM agent_suggestions s
                     WHERE s.note_id = n.id
                       AND s.archived_at IS NULL
                       AND s.status = 'pending'
                     ORDER BY s.created_at DESC
                     LIMIT 1
                   ) AS suggestion_id,
                   (
                     SELECT s.summary
                     FROM agent_suggestions s
                     WHERE s.note_id = n.id
                       AND s.archived_at IS NULL
                       AND s.status = 'pending'
                     ORDER BY s.created_at DESC
                     LIMIT 1
                   ) AS suggestion_summary
            FROM notes n
            WHERE n.is_limbo = 1
              AND n.archived_at IS NULL
            ORDER BY n.created_at DESC;
            """;

        var list = new List<LimboNoteRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new LimboNoteRecord
            {
                Id = reader.GetString(0),
                OriginalText = reader.GetString(1),
                CreatedAt = reader.GetString(2),
                SuggestionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                SuggestionSummary = reader.IsDBNull(4) ? null : reader.GetString(4),
            });
        }

        return list;
    }
}
