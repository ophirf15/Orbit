using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Operator-initiated project merge: preview counts, move rows to target, transfer aliases, archive source.
/// Never auto-merges; no personal site names baked in.
/// </summary>
public sealed class ProjectMergeStore
{
    private readonly SqliteConnectionFactory _factory;

    public ProjectMergeStore(SqliteConnectionFactory factory) => _factory = factory;

    public ProjectMergePreview Preview(string sourceProjectId, string targetProjectId)
    {
        var source = RequireActiveProject(sourceProjectId);
        var target = RequireActiveProject(targetProjectId);
        if (string.Equals(source.Id, target.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source and target must be different projects.");
        }

        using var connection = _factory.CreateConnection();
        return new ProjectMergePreview
        {
            SourceProjectId = source.Id,
            SourceName = source.Name,
            TargetProjectId = target.Id,
            TargetName = target.Name,
            TaskCount = Count(connection, "SELECT COUNT(*) FROM tasks WHERE project_id = $p AND archived_at IS NULL;", source.Id),
            NoteCount = Count(connection, "SELECT COUNT(*) FROM notes WHERE project_id = $p AND archived_at IS NULL;", source.Id),
            WorkstreamCount = Count(connection, "SELECT COUNT(*) FROM workstreams WHERE project_id = $p AND archived_at IS NULL;", source.Id),
            FileLinkCount = Count(connection, "SELECT COUNT(*) FROM file_project_links WHERE project_id = $p;", source.Id),
            EmailLinkCount = Count(connection, "SELECT COUNT(*) FROM email_project_links WHERE project_id = $p;", source.Id),
            ContactLinkCount = CountContactLinks(connection, source.Id),
            AliasCount = Count(connection, "SELECT COUNT(*) FROM project_aliases WHERE project_id = $p;", source.Id),
            BlockerCount = Count(connection, "SELECT COUNT(*) FROM blockers WHERE project_id = $p AND archived_at IS NULL;", source.Id),
            FolderCount = Count(connection, "SELECT COUNT(*) FROM project_folders WHERE project_id = $p;", source.Id),
            Warnings = BuildWarnings(connection, source, target),
        };
    }

    public ProjectMergeResult Merge(
        string sourceProjectId,
        string targetProjectId,
        bool force = false,
        string? actor = null)
    {
        var preview = Preview(sourceProjectId, targetProjectId);
        if (preview.Warnings.Count > 0 && !force)
        {
            throw new InvalidOperationException(
                "Merge has warnings; pass force=true after operator review. "
                + string.Join(" ", preview.Warnings));
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var requestedBy = string.IsNullOrWhiteSpace(actor) ? "operator" : actor.Trim();

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        var moved = new ProjectMergeMovedCounts();
        moved.Tasks = Exec(
            connection,
            tx,
            "UPDATE tasks SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Notes = Exec(
            connection,
            tx,
            "UPDATE notes SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Workstreams = Exec(
            connection,
            tx,
            "UPDATE workstreams SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Blockers = Exec(
            connection,
            tx,
            "UPDATE blockers SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Suggestions = Exec(
            connection,
            tx,
            "UPDATE agent_suggestions SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Extractions = Exec(
            connection,
            tx,
            "UPDATE email_extractions SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.Folders = Exec(
            connection,
            tx,
            """
            UPDATE project_folders
            SET project_id = $target, is_home = 0, updated_at = $t
            WHERE project_id = $source;
            """,
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);

        moved.FileLinks = MoveUniqueLinks(
            connection,
            tx,
            "file_project_links",
            "file_artifact_id",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.EmailLinks = MoveUniqueLinks(
            connection,
            tx,
            "email_project_links",
            "email_artifact_id",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        moved.ContactLinks = MoveContactLinks(connection, tx, preview.SourceProjectId, preview.TargetProjectId, now);
        moved.Aliases = TransferAliases(connection, tx, preview.SourceProjectId, preview.TargetProjectId, now);
        moved.SourceNameAliasAdded = TryAddSourceNameAsAlias(
            connection,
            tx,
            preview.SourceProjectId,
            preview.SourceName,
            preview.TargetProjectId,
            preview.TargetName,
            now);

        // Calendar / event entity links pointing at the source project
        moved.EventLinks = Exec(
            connection,
            tx,
            """
            UPDATE event_entity_links
            SET entity_id = $target
            WHERE entity_type = 'project' AND entity_id = $source
              AND NOT EXISTS (
                SELECT 1 FROM event_entity_links x
                WHERE x.calendar_event_id = event_entity_links.calendar_event_id
                  AND x.entity_type = 'project'
                  AND x.entity_id = $target);
            """,
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        Exec(
            connection,
            tx,
            """
            DELETE FROM event_entity_links
            WHERE entity_type = 'project' AND entity_id = $source;
            """,
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);

        // relationships.project_id + person→project edges
        Exec(
            connection,
            tx,
            "UPDATE relationships SET project_id = $target, updated_at = $t WHERE project_id = $source AND archived_at IS NULL;",
            preview.SourceProjectId,
            preview.TargetProjectId,
            now);
        Exec(
            connection,
            tx,
            """
            UPDATE relationships
            SET target_id = $target, updated_at = $t
            WHERE target_type = $project AND target_id = $source AND archived_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM relationships r2
                WHERE r2.source_type = relationships.source_type
                  AND r2.source_id = relationships.source_id
                  AND r2.target_type = $project
                  AND r2.target_id = $target
                  AND r2.relationship_type = relationships.relationship_type
                  AND r2.archived_at IS NULL);
            """,
            preview.SourceProjectId,
            preview.TargetProjectId,
            now,
            projectType: EntityTypes.Project);
        Exec(
            connection,
            tx,
            """
            UPDATE relationships
            SET archived_at = $t, updated_at = $t
            WHERE target_type = $project AND target_id = $source AND archived_at IS NULL;
            """,
            preview.SourceProjectId,
            preview.TargetProjectId,
            now,
            projectType: EntityTypes.Project);

        // Soft-archive source only (children already moved — cascade would be a no-op).
        using (var archive = connection.CreateCommand())
        {
            archive.Transaction = tx;
            archive.CommandText =
                """
                UPDATE projects
                SET archived_at = $t, updated_at = $t, status = 'archived', in_orbit = 0
                WHERE id = $source AND archived_at IS NULL;
                """;
            archive.Parameters.AddWithValue("$t", now);
            archive.Parameters.AddWithValue("$source", preview.SourceProjectId);
            if (archive.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("Source project was not found or already archived during merge.");
            }
        }

        WriteAudit(
            connection,
            tx,
            "project.merged",
            preview.SourceProjectId,
            requestedBy,
            new
            {
                sourceProjectId = preview.SourceProjectId,
                sourceName = preview.SourceName,
                targetProjectId = preview.TargetProjectId,
                targetName = preview.TargetName,
                force,
                moved,
            },
            now);

        WriteAudit(
            connection,
            tx,
            "project.merge.received",
            preview.TargetProjectId,
            requestedBy,
            new
            {
                sourceProjectId = preview.SourceProjectId,
                sourceName = preview.SourceName,
                targetProjectId = preview.TargetProjectId,
                force,
                moved,
            },
            now);

        tx.Commit();

        return new ProjectMergeResult
        {
            SourceProjectId = preview.SourceProjectId,
            SourceName = preview.SourceName,
            TargetProjectId = preview.TargetProjectId,
            TargetName = preview.TargetName,
            ArchivedSource = true,
            Moved = moved,
            MergedAt = now,
        };
    }

    private static IReadOnlyList<string> BuildWarnings(
        SqliteConnection connection,
        (string Id, string Name) source,
        (string Id, string Name) target)
    {
        var warnings = new List<string>();
        var sourceHome = Count(connection, "SELECT COUNT(*) FROM project_folders WHERE project_id = $p AND is_home = 1;", source.Id);
        var targetHome = Count(connection, "SELECT COUNT(*) FROM project_folders WHERE project_id = $p AND is_home = 1;", target.Id);
        if (sourceHome > 0 && targetHome > 0)
        {
            warnings.Add(
                "Both projects have a home folder; source folders will attach to the target as non-home paths.");
        }

        return warnings;
    }

    private static int MoveUniqueLinks(
        SqliteConnection connection,
        SqliteTransaction tx,
        string table,
        string artifactColumn,
        string sourceId,
        string targetId,
        string now)
    {
        // Drop duplicates already on target
        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText =
                $"""
                DELETE FROM {table}
                WHERE project_id = $source
                  AND {artifactColumn} IN (
                    SELECT {artifactColumn} FROM {table} WHERE project_id = $target);
                """;
            del.Parameters.AddWithValue("$source", sourceId);
            del.Parameters.AddWithValue("$target", targetId);
            del.ExecuteNonQuery();
        }

        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            $"""
            UPDATE {table}
            SET project_id = $target
            WHERE project_id = $source;
            """;
        upd.Parameters.AddWithValue("$source", sourceId);
        upd.Parameters.AddWithValue("$target", targetId);
        _ = now;
        return upd.ExecuteNonQuery();
    }

    private static int MoveContactLinks(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourceId,
        string targetId,
        string now)
    {
        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            """
            UPDATE relationships
            SET target_id = $target, project_id = $target, updated_at = $t
            WHERE source_type = $person
              AND target_type = $project
              AND target_id = $source
              AND archived_at IS NULL
              AND NOT EXISTS (
                SELECT 1 FROM relationships r2
                WHERE r2.source_type = $person
                  AND r2.source_id = relationships.source_id
                  AND r2.target_type = $project
                  AND r2.target_id = $target
                  AND r2.archived_at IS NULL);
            """;
        upd.Parameters.AddWithValue("$person", EntityTypes.Person);
        upd.Parameters.AddWithValue("$project", EntityTypes.Project);
        upd.Parameters.AddWithValue("$source", sourceId);
        upd.Parameters.AddWithValue("$target", targetId);
        upd.Parameters.AddWithValue("$t", now);
        var moved = upd.ExecuteNonQuery();

        using var archiveDupes = connection.CreateCommand();
        archiveDupes.Transaction = tx;
        archiveDupes.CommandText =
            """
            UPDATE relationships
            SET archived_at = $t, updated_at = $t
            WHERE source_type = $person
              AND target_type = $project
              AND target_id = $source
              AND archived_at IS NULL;
            """;
        archiveDupes.Parameters.AddWithValue("$person", EntityTypes.Person);
        archiveDupes.Parameters.AddWithValue("$project", EntityTypes.Project);
        archiveDupes.Parameters.AddWithValue("$source", sourceId);
        archiveDupes.Parameters.AddWithValue("$t", now);
        archiveDupes.ExecuteNonQuery();
        return moved;
    }

    private static int TransferAliases(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourceId,
        string targetId,
        string now)
    {
        // normalized_alias is globally unique — safe to re-point to target.
        using var upd = connection.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText =
            """
            UPDATE project_aliases
            SET project_id = $target
            WHERE project_id = $source;
            """;
        upd.Parameters.AddWithValue("$source", sourceId);
        upd.Parameters.AddWithValue("$target", targetId);
        _ = now;
        return upd.ExecuteNonQuery();
    }

    private static bool TryAddSourceNameAsAlias(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sourceId,
        string sourceName,
        string targetId,
        string targetName,
        string now)
    {
        var normalized = ProjectIdentityMatcher.Normalize(sourceName);
        var targetNorm = ProjectIdentityMatcher.Normalize(targetName);
        if (normalized.Length == 0 || string.Equals(normalized, targetNorm, StringComparison.Ordinal))
        {
            return false;
        }

        using (var clash = connection.CreateCommand())
        {
            clash.Transaction = tx;
            clash.CommandText = "SELECT 1 FROM project_aliases WHERE normalized_alias = $n LIMIT 1;";
            clash.Parameters.AddWithValue("$n", normalized);
            if (clash.ExecuteScalar() is not null)
            {
                return false;
            }
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO project_aliases (id, project_id, alias, normalized_alias, created_at)
            VALUES ($id, $p, $alias, $n, $t);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        insert.Parameters.AddWithValue("$p", targetId);
        insert.Parameters.AddWithValue("$alias", sourceName.Trim());
        insert.Parameters.AddWithValue("$n", normalized);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();
        _ = sourceId;
        return true;
    }

    private static int CountContactLinks(SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM relationships
            WHERE source_type = $person
              AND target_type = $project
              AND target_id = $p
              AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$person", EntityTypes.Person);
        cmd.Parameters.AddWithValue("$project", EntityTypes.Project);
        cmd.Parameters.AddWithValue("$p", projectId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static int Count(SqliteConnection connection, string sql, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", projectId);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static int Exec(
        SqliteConnection connection,
        SqliteTransaction tx,
        string sql,
        string sourceId,
        string targetId,
        string now,
        string? projectType = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$source", sourceId);
        cmd.Parameters.AddWithValue("$target", targetId);
        cmd.Parameters.AddWithValue("$t", now);
        if (projectType is not null)
        {
            cmd.Parameters.AddWithValue("$project", projectType);
        }

        return cmd.ExecuteNonQuery();
    }

    private (string Id, string Name) RequireActiveProject(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, name FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId.Trim());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            throw new ArgumentException("Project was not found or is archived.", nameof(projectId));
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction tx,
        string eventType,
        string entityId,
        string actor,
        object detail,
        string now)
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
        audit.Parameters.AddWithValue("$ent", EntityTypes.Project);
        audit.Parameters.AddWithValue("$eid", entityId);
        audit.Parameters.AddWithValue("$actor", actor);
        audit.Parameters.AddWithValue("$detail", AuditDetailJson.Serialize(detail, provenance: null));
        audit.Parameters.AddWithValue("$t", now);
        audit.ExecuteNonQuery();
    }
}

public sealed class ProjectMergePreview
{
    public required string SourceProjectId { get; init; }

    public required string SourceName { get; init; }

    public required string TargetProjectId { get; init; }

    public required string TargetName { get; init; }

    public int TaskCount { get; init; }

    public int NoteCount { get; init; }

    public int WorkstreamCount { get; init; }

    public int FileLinkCount { get; init; }

    public int EmailLinkCount { get; init; }

    public int ContactLinkCount { get; init; }

    public int AliasCount { get; init; }

    public int BlockerCount { get; init; }

    public int FolderCount { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ProjectMergeMovedCounts
{
    public int Tasks { get; set; }

    public int Notes { get; set; }

    public int Workstreams { get; set; }

    public int Blockers { get; set; }

    public int Suggestions { get; set; }

    public int Extractions { get; set; }

    public int Folders { get; set; }

    public int FileLinks { get; set; }

    public int EmailLinks { get; set; }

    public int ContactLinks { get; set; }

    public int Aliases { get; set; }

    public int EventLinks { get; set; }

    public bool SourceNameAliasAdded { get; set; }
}

public sealed class ProjectMergeResult
{
    public required string SourceProjectId { get; init; }

    public required string SourceName { get; init; }

    public required string TargetProjectId { get; init; }

    public required string TargetName { get; init; }

    public bool ArchivedSource { get; init; }

    public required ProjectMergeMovedCounts Moved { get; init; }

    public required string MergedAt { get; init; }
}
