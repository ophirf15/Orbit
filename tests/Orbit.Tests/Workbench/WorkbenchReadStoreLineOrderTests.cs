using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Tests.Workbench;

public sealed class WorkbenchReadStoreLineOrderTests
{
    [Fact]
    public void NewestTask_AppearsEvenWhenOlderOpenTasksExist()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        new DemoGraphSeed(factory).SeedIfEmpty();

        using var connection = factory.CreateConnection();
        var projectId = Scalar(connection, "SELECT id FROM projects WHERE archived_at IS NULL LIMIT 1;");
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        for (var i = 0; i < 10; i++)
        {
            InsertTask(connection, projectId!, $"Old {i}", status: "active", updatedAt: DateTime.UtcNow.AddMinutes(-60 + i));
        }

        var newestId = Guid.NewGuid().ToString("D");
        InsertTask(connection, projectId!, "Brand new capture", status: "not_started", updatedAt: DateTime.UtcNow, id: newestId);

        var store = new WorkbenchReadStore(factory);
        var snap = store.GetSnapshot();
        var cell = Assert.Single(snap.Cells.Where(c => c.Id == projectId));
        Assert.Contains(cell.Lines, l => l.TaskId == newestId);
        Assert.Equal(newestId, cell.Lines[0].TaskId);
    }

    private static string Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? string.Empty;
    }

    private static void InsertTask(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string projectId,
        string title,
        string status,
        DateTime updatedAt,
        string? id = null)
    {
        id ??= Guid.NewGuid().ToString("D");
        var t = updatedAt.ToString("O");
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tasks (
              id, project_id, workstream_id, title, body, status, priority,
              next_action, waiting_on_person_id, waiting_on_organization_id,
              created_at, updated_at, archived_at)
            VALUES ($id, $p, NULL, $title, NULL, $status, NULL, NULL, NULL, NULL, $t, $t, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$t", t);
        cmd.ExecuteNonQuery();
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitWbLineTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "data", OrbitDbPaths.DatabaseFileName);

        public TempDb() => Directory.CreateDirectory(Path.Combine(Root, "data"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
