using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Orbit.Core.Host;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Files;

public sealed class FileIndexService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly ProjectFolderStore _folders;
    private readonly IExternalFileCapability _external;
    private readonly FileTextExtractionPipeline _extractors = new();

    public FileIndexService(
        SqliteConnectionFactory factory,
        ProjectFolderStore folders,
        IExternalFileCapability external)
    {
        _factory = factory;
        _folders = folders;
        _external = external;
    }

    public int ReindexFolder(string folderId) => ReindexFolderDetailed(folderId).TouchedCount;

    public FileReindexResult ReindexFolderDetailed(string folderId, FileReindexOptions? options = null)
    {
        options ??= new FileReindexOptions();
        var folder = _folders.Get(folderId)
            ?? throw new ArgumentException("Folder was not found.", nameof(folderId));

        if (!Directory.Exists(folder.RootPath))
        {
            _folders.MarkIndexed(folderId, FolderAvailability.Missing);
            return new FileReindexResult
            {
                Warning = "Folder root is missing on disk.",
            };
        }

        var root = PathSafety.NormalizeFullPath(folder.RootPath);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new FileReindexResult();

        foreach (var path in EnumerateFilesSafe(root, result))
        {
            try
            {
                var outcome = UpsertFile(folder, path, options);
                if (outcome.FileId is not null)
                {
                    seenIds.Add(outcome.FileId);
                    result.TouchedCount++;
                    if (outcome.IsOfflinePlaceholder)
                    {
                        result.OfflinePlaceholderCount++;
                    }

                    if (outcome.SkippedUnchanged)
                    {
                        result.SkippedUnchangedCount++;
                    }
                    else if (outcome.ExtractedText)
                    {
                        result.ExtractedCount++;
                    }

                    result.AddSampleRelativePath(ToRelativePath(root, path));
                }
            }
            catch (Exception)
            {
                // continue indexing other files
            }
        }

        PruneMissing(folder.Id, folder.ProjectId, seenIds);
        RebuildSearchForProject(folder.ProjectId);
        _folders.MarkIndexed(folderId, FolderAvailability.Available);
        result.FinalizeWarning();
        return result;
    }

    public IReadOnlyList<FileSearchHit> ListForProject(string projectId, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT fa.id, fa.path, COALESCE(fa.display_name, fa.path), fa.extension,
                   substr(COALESCE(fa.indexed_text, ''), 1, 160), fpl.project_id
            FROM file_artifacts fa
            INNER JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
            WHERE fa.archived_at IS NULL
              AND fpl.project_id = $p
            ORDER BY fa.path COLLATE NOCASE
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadHits(cmd);
    }

    public IReadOnlyList<FileSearchHit> Search(string query, string? projectId = null, int limit = 50)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var connection = _factory.CreateConnection();

        // Prefer FTS when available; fall back to LIKE on projection / indexed_text.
        try
        {
            using var fts = connection.CreateCommand();
            fts.CommandText =
                """
                SELECT fa.id, fa.path, COALESCE(fa.display_name, fa.path), fa.extension,
                       substr(COALESCE(fa.indexed_text, ''), 1, 160), fpl.project_id
                FROM search_documents_fts fts
                INNER JOIN search_documents sd ON sd.rowid = fts.rowid
                INNER JOIN file_artifacts fa ON fa.id = sd.entity_id
                LEFT JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
                WHERE fts MATCH $q
                  AND sd.entity_type = 'file'
                  AND fa.archived_at IS NULL
                  AND ($p IS NULL OR fpl.project_id = $p)
                LIMIT $limit;
                """;
            fts.Parameters.AddWithValue("$q", ToFtsQuery(query));
            fts.Parameters.AddWithValue("$p", (object?)projectId ?? DBNull.Value);
            fts.Parameters.AddWithValue("$limit", limit);
            return ReadHits(fts);
        }
        catch (SqliteException)
        {
            using var like = connection.CreateCommand();
            like.CommandText =
                """
                SELECT fa.id, fa.path, COALESCE(fa.display_name, fa.path), fa.extension,
                       substr(COALESCE(fa.indexed_text, ''), 1, 160), fpl.project_id
                FROM file_artifacts fa
                LEFT JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
                WHERE fa.archived_at IS NULL
                  AND (
                    fa.display_name LIKE $like ESCAPE '\'
                    OR fa.path LIKE $like ESCAPE '\'
                    OR IFNULL(fa.indexed_text, '') LIKE $like ESCAPE '\'
                  )
                  AND ($p IS NULL OR fpl.project_id = $p)
                ORDER BY fa.display_name COLLATE NOCASE
                LIMIT $limit;
                """;
            like.Parameters.AddWithValue("$like", "%" + EscapeLike(query.Trim()) + "%");
            like.Parameters.AddWithValue("$p", (object?)projectId ?? DBNull.Value);
            like.Parameters.AddWithValue("$limit", limit);
            return ReadHits(like);
        }
    }

    public IndexedFileRecord? Get(string fileId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, path, display_name, extension, size_bytes, modified_at, content_hash,
                   mime_type, project_folder_id, availability,
                   substr(IFNULL(indexed_text, ''), 1, 65536)
            FROM file_artifacts
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", fileId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new IndexedFileRecord
        {
            Id = reader.GetString(0),
            Path = reader.GetString(1),
            DisplayName = reader.IsDBNull(2) ? reader.GetString(1) : reader.GetString(2),
            Extension = reader.IsDBNull(3) ? null : reader.GetString(3),
            SizeBytes = reader.IsDBNull(4) ? null : reader.GetInt64(4),
            ModifiedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            ContentHash = reader.IsDBNull(6) ? null : reader.GetString(6),
            MimeType = reader.IsDBNull(7) ? null : reader.GetString(7),
            ProjectFolderId = reader.IsDBNull(8) ? null : reader.GetString(8),
            Availability = reader.IsDBNull(9) ? null : reader.GetString(9),
            IndexedTextPreview = reader.IsDBNull(10) ? null : reader.GetString(10),
        };
    }

    public void LinkToEntity(string fileId, string entityType, string entityId)
    {
        using var connection = _factory.CreateConnection();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM file_artifacts WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        exists.Parameters.AddWithValue("$id", fileId);
        if (exists.ExecuteScalar() is null)
        {
            throw new ArgumentException("File was not found.", nameof(fileId));
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO file_entity_links (id, file_artifact_id, entity_type, entity_id, created_at)
            VALUES ($id, $file, $type, $entity, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$type", entityType);
        cmd.Parameters.AddWithValue("$entity", entityId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void LinkToProject(string fileId, string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT OR IGNORE INTO file_project_links (id, file_artifact_id, project_id, created_at)
            VALUES ($id, $file, $project, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string EntityType, string EntityId)> ListLinks(string fileId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT entity_type, entity_id FROM file_entity_links WHERE file_artifact_id = $id
            UNION ALL
            SELECT 'project', project_id FROM file_project_links WHERE file_artifact_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", fileId);
        var list = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }

        return list;
    }

    private UpsertOutcome UpsertFile(ProjectFolderRecord folder, string path, FileReindexOptions options)
    {
        var full = PathSafety.NormalizeFullPath(path);
        var stat = _external.Stat(full);
        if (stat is null || stat.IsDirectory)
        {
            return default;
        }

        var looksOffline = string.Equals(
            stat.Availability,
            FolderAvailability.OfflinePlaceholder,
            StringComparison.Ordinal);
        if (looksOffline && !options.IncludeOfflinePlaceholders)
        {
            return default;
        }

        var now = DateTime.UtcNow.ToString("O");
        var modified = stat.ModifiedAtUtc.ToString("O");
        var display = stat.FileName;
        var mime = GuessMime(stat.Extension);

        using var connection = _factory.CreateConnection();
        string? existingId = null;
        long? existingSize = null;
        string? existingModified = null;
        string? existingHash = null;
        string? existingText = null;
        using (var find = connection.CreateCommand())
        {
            find.CommandText =
                """
                SELECT id, size_bytes, modified_at, content_hash, indexed_text
                FROM file_artifacts
                WHERE path = $path
                LIMIT 1;
                """;
            find.Parameters.AddWithValue("$path", full);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                existingId = reader.GetString(0);
                existingSize = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                existingModified = reader.IsDBNull(2) ? null : reader.GetString(2);
                existingHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                existingText = reader.IsDBNull(4) ? null : reader.GetString(4);
            }
        }

        // Cheap skip: same size + mtime → do not open/hash/extract.
        if (existingId is not null
            && existingSize == stat.SizeBytes
            && string.Equals(existingModified, modified, StringComparison.Ordinal))
        {
            LinkToProject(existingId, folder.ProjectId);
            TouchFolderLink(connection, existingId, folder.Id, now);
            return new UpsertOutcome(
                existingId,
                SkippedUnchanged: true,
                ExtractedText: false,
                IsOfflinePlaceholder: looksOffline);
        }

        string? text = existingText;
        string? hash = null;
        var availability = stat.Availability;
        var extracted = false;

        try
        {
            using var stream = _external.OpenRead(full);
            hash = ComputeSha256(stream);

            if (existingId is not null
                && !string.IsNullOrWhiteSpace(existingHash)
                && string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                // Content unchanged; refresh metadata only, keep indexed_text.
                using var meta = connection.CreateCommand();
                meta.CommandText =
                    """
                    UPDATE file_artifacts
                    SET size_bytes = $size,
                        modified_at = $mod,
                        updated_at = $t,
                        project_folder_id = $folder,
                        availability = $avail,
                        display_name = $name,
                        extension = $ext,
                        mime_type = $mime,
                        archived_at = NULL
                    WHERE id = $id;
                    """;
                meta.Parameters.AddWithValue("$size", stat.SizeBytes);
                meta.Parameters.AddWithValue("$mod", modified);
                meta.Parameters.AddWithValue("$t", now);
                meta.Parameters.AddWithValue("$folder", folder.Id);
                meta.Parameters.AddWithValue("$avail", availability);
                meta.Parameters.AddWithValue("$name", display);
                meta.Parameters.AddWithValue("$ext", stat.Extension);
                meta.Parameters.AddWithValue("$mime", (object?)mime ?? DBNull.Value);
                meta.Parameters.AddWithValue("$id", existingId);
                meta.ExecuteNonQuery();
                LinkToProject(existingId, folder.ProjectId);
                return new UpsertOutcome(
                    existingId,
                    SkippedUnchanged: true,
                    ExtractedText: false,
                    IsOfflinePlaceholder: false);
            }

            if (stream.CanSeek)
            {
                stream.Position = 0;
            }

            text = _extractors.TryExtract(full, stat.Extension, stream);
            extracted = true;
        }
        catch (IOException)
        {
            availability = FolderAvailability.OfflinePlaceholder;
            if (!options.IncludeOfflinePlaceholders)
            {
                return default;
            }
        }

        var id = existingId ?? Guid.NewGuid().ToString("D");
        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText =
                """
                INSERT INTO file_artifacts (
                  id, path, display_name, content_hash, mime_type, size_bytes,
                  created_at, updated_at, extension, modified_at, project_folder_id, availability, indexed_text)
                VALUES (
                  $id, $path, $name, $hash, $mime, $size,
                  $t, $t, $ext, $mod, $folder, $avail, $text)
                ON CONFLICT(id) DO UPDATE SET
                  path = excluded.path,
                  display_name = excluded.display_name,
                  content_hash = excluded.content_hash,
                  mime_type = excluded.mime_type,
                  size_bytes = excluded.size_bytes,
                  updated_at = excluded.updated_at,
                  extension = excluded.extension,
                  modified_at = excluded.modified_at,
                  project_folder_id = excluded.project_folder_id,
                  availability = excluded.availability,
                  indexed_text = excluded.indexed_text,
                  archived_at = NULL;
                """;
            upsert.Parameters.AddWithValue("$id", id);
            upsert.Parameters.AddWithValue("$path", full);
            upsert.Parameters.AddWithValue("$name", display);
            upsert.Parameters.AddWithValue("$hash", (object?)hash ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$mime", (object?)mime ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$size", stat.SizeBytes);
            upsert.Parameters.AddWithValue("$t", now);
            upsert.Parameters.AddWithValue("$ext", stat.Extension);
            upsert.Parameters.AddWithValue("$mod", modified);
            upsert.Parameters.AddWithValue("$folder", folder.Id);
            upsert.Parameters.AddWithValue("$avail", availability);
            upsert.Parameters.AddWithValue("$text", (object?)text ?? DBNull.Value);
            upsert.ExecuteNonQuery();
        }

        LinkToProject(id, folder.ProjectId);
        var offline = string.Equals(availability, FolderAvailability.OfflinePlaceholder, StringComparison.Ordinal);
        return new UpsertOutcome(id, SkippedUnchanged: false, ExtractedText: extracted, IsOfflinePlaceholder: offline);
    }

    private static void TouchFolderLink(SqliteConnection connection, string fileId, string folderId, string now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE file_artifacts
            SET project_folder_id = $folder, updated_at = $t, archived_at = NULL
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$folder", folderId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$id", fileId);
        cmd.ExecuteNonQuery();
    }

    private readonly record struct UpsertOutcome(
        string? FileId,
        bool SkippedUnchanged,
        bool ExtractedText,
        bool IsOfflinePlaceholder);

    private void PruneMissing(string folderId, string projectId, HashSet<string> seenIds)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM file_artifacts
            WHERE project_folder_id = $folder AND archived_at IS NULL;
            """;
        cmd.Parameters.AddWithValue("$folder", folderId);
        var stale = new List<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);
                if (!seenIds.Contains(id))
                {
                    stale.Add(id);
                }
            }
        }

        foreach (var id in stale)
        {
            using var archive = connection.CreateCommand();
            archive.CommandText =
                "UPDATE file_artifacts SET archived_at = $t, updated_at = $t WHERE id = $id;";
            archive.Parameters.AddWithValue("$id", id);
            archive.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            archive.ExecuteNonQuery();
        }

        _ = projectId;
    }

    private void RebuildSearchForProject(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText =
                """
                DELETE FROM search_documents
                WHERE entity_type = 'file'
                  AND entity_id IN (
                    SELECT fa.id FROM file_artifacts fa
                    INNER JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
                    WHERE fpl.project_id = $p
                  );
                """;
            del.Parameters.AddWithValue("$p", projectId);
            del.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
                SELECT fa.id, 'file', fa.id, $p, COALESCE(fa.display_name, fa.path),
                       COALESCE(fa.indexed_text, ''), fa.updated_at
                FROM file_artifacts fa
                INNER JOIN file_project_links fpl ON fpl.file_artifact_id = fa.id
                WHERE fpl.project_id = $p AND fa.archived_at IS NULL;
                """;
            insert.Parameters.AddWithValue("$p", projectId);
            insert.ExecuteNonQuery();
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
            // projection table remains
        }

        tx.Commit();
    }

    /// <summary>
    /// Depth-first walk using file attributes (more reliable than Exists for cloud placeholders).
    /// Soft-skips entire directories on ACL/cloud IO failures and records them on <paramref name="result"/>.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, FileReindexResult result)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(dir).EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ExternalFileService.IsCloudOrIoSoftFailure(ex))
            {
                // Not the walk root: skipping a subtree is the "root files only" symptom when
                // OneDrive/ACL denies Enumerate on nested folders.
                if (!string.Equals(dir, root, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddSoftSkippedDirectory(dir);
                }

                continue;
            }

            foreach (var entry in entries)
            {
                string full;
                try
                {
                    full = PathSafety.NormalizeFullPath(entry.FullName);
                }
                catch (Exception)
                {
                    continue;
                }

                bool isDirectory;
                try
                {
                    isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
                }
                catch (Exception ex) when (ExternalFileService.IsCloudOrIoSoftFailure(ex))
                {
                    // Fall back to Exists; if both fail, drop the entry rather than mis-classify.
                    isDirectory = Directory.Exists(full);
                    if (!isDirectory && !File.Exists(full))
                    {
                        continue;
                    }
                }

                if (isDirectory)
                {
                    stack.Push(full);
                }
                else
                {
                    yield return full;
                }
            }
        }
    }

    public static string ToRelativePath(string root, string fullPath)
    {
        var normalizedRoot = PathSafety.NormalizeFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedFull = PathSafety.NormalizeFullPath(fullPath);
        if (!normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(normalizedFull);
        }

        var relative = normalizedFull[normalizedRoot.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.IsNullOrEmpty(relative)
            ? Path.GetFileName(normalizedFull)
            : relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static List<FileSearchHit> ReadHits(SqliteCommand cmd)
    {
        var list = new List<FileSearchHit>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FileSearchHit
            {
                Id = reader.GetString(0),
                Path = reader.GetString(1),
                DisplayName = reader.GetString(2),
                Extension = reader.IsDBNull(3) ? null : reader.GetString(3),
                Snippet = reader.IsDBNull(4) ? null : reader.GetString(4),
                ProjectId = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return list;
    }

    private static string ComputeSha256(Stream stream)
    {
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ToFtsQuery(string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return "\"\"";
        }

        return string.Join(" AND ", tokens.Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string? GuessMime(string extension) => extension.ToLowerInvariant() switch
    {
        "pdf" => "application/pdf",
        "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "txt" => "text/plain",
        "csv" => "text/csv",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "msg" => "application/vnd.ms-outlook",
        _ => null,
    };
}
