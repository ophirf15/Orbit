using Microsoft.Data.Sqlite;
using Orbit.Core.Host;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Files;

public sealed class ProjectFolderStore
{
    private readonly SqliteConnectionFactory _factory;

    public ProjectFolderStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<string> ListActiveRootPaths()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT root_path FROM project_folders
            WHERE archived_at IS NULL;
            """;
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Writable <c>.orbit</c> roots for projects that have a primary home folder.</summary>
    public IReadOnlyList<string> ListActiveHomeSandboxRoots()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT root_path FROM project_folders
            WHERE archived_at IS NULL AND is_home = 1;
            """;
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(OrbitHomeSandbox.GetSandboxRoot(reader.GetString(0)));
        }

        return list;
    }

    public ProjectFolderRecord? GetHome(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, root_path, availability, last_indexed_at, is_home
            FROM project_folders
            WHERE project_id = $p AND is_home = 1 AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", projectId.Trim());
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public IReadOnlyList<ProjectFolderRecord> ListForProject(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, root_path, availability, last_indexed_at, is_home
            FROM project_folders
            WHERE project_id = $p AND archived_at IS NULL
            ORDER BY is_home DESC, root_path COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        return ReadFolders(cmd);
    }

    public IReadOnlyList<ProjectFolderRecord> ListAll()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, root_path, availability, last_indexed_at, is_home
            FROM project_folders
            WHERE archived_at IS NULL
            ORDER BY root_path COLLATE NOCASE;
            """;
        return ReadFolders(cmd);
    }

    public ProjectFolderRecord Attach(string projectId, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var full = PathSafety.NormalizeFullPath(rootPath);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Folder not found: {full}");
        }

        EnsureProjectExists(projectId);

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");

        using var connection = _factory.CreateConnection();
        using var existing = connection.CreateCommand();
        existing.CommandText =
            """
            SELECT id, project_id, root_path, availability, last_indexed_at, is_home
            FROM project_folders
            WHERE project_id = $p AND root_path = $path AND archived_at IS NULL
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$p", projectId);
        existing.Parameters.AddWithValue("$path", full);
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                return Map(reader);
            }
        }

        using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO project_folders (id, project_id, root_path, availability, created_at, updated_at, is_home)
            VALUES ($id, $p, $path, $avail, $t, $t, 0);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$p", projectId);
        insert.Parameters.AddWithValue("$path", full);
        insert.Parameters.AddWithValue("$avail", FolderAvailability.Available);
        insert.Parameters.AddWithValue("$t", now);
        insert.ExecuteNonQuery();

        return new ProjectFolderRecord
        {
            Id = id,
            ProjectId = projectId,
            RootPath = full,
            Availability = FolderAvailability.Available,
            LastIndexedAt = null,
            IsHome = false,
        };
    }

    /// <summary>
    /// Attaches (or reuses) a folder as the project's primary home, clears prior home flags,
    /// and ensures the <c>.orbit</c> writable sandbox exists.
    /// </summary>
    public ProjectFolderRecord SetHome(string projectId, string rootPath)
    {
        var folder = Attach(projectId, rootPath);
        OrbitHomeSandbox.EnsureCreated(folder.RootPath);

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText =
                """
                UPDATE project_folders
                SET is_home = 0, updated_at = $t
                WHERE project_id = $p AND archived_at IS NULL AND is_home = 1;
                """;
            clear.Parameters.AddWithValue("$p", projectId.Trim());
            clear.Parameters.AddWithValue("$t", now);
            clear.ExecuteNonQuery();
        }

        using (var set = connection.CreateCommand())
        {
            set.Transaction = tx;
            set.CommandText =
                """
                UPDATE project_folders
                SET is_home = 1, updated_at = $t
                WHERE id = $id;
                """;
            set.Parameters.AddWithValue("$id", folder.Id);
            set.Parameters.AddWithValue("$t", now);
            set.ExecuteNonQuery();
        }

        tx.Commit();

        return Get(folder.Id)
            ?? new ProjectFolderRecord
            {
                Id = folder.Id,
                ProjectId = folder.ProjectId,
                RootPath = folder.RootPath,
                Availability = folder.Availability,
                LastIndexedAt = folder.LastIndexedAt,
                IsHome = true,
            };
    }

    public void MarkIndexed(string folderId, string availability)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE project_folders
            SET last_indexed_at = $t, availability = $a, updated_at = $t
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        cmd.Parameters.AddWithValue("$a", availability);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public ProjectFolderRecord? Get(string folderId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, root_path, availability, last_indexed_at, is_home
            FROM project_folders
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", folderId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    private void EnsureProjectExists(string projectId)
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

    private static List<ProjectFolderRecord> ReadFolders(SqliteCommand cmd)
    {
        var list = new List<ProjectFolderRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    private static ProjectFolderRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProjectId = reader.GetString(1),
        RootPath = reader.GetString(2),
        Availability = reader.GetString(3),
        LastIndexedAt = reader.IsDBNull(4) ? null : reader.GetString(4),
        IsHome = !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
    };
}
