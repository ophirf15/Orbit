using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Email;

namespace Orbit.Tests.Workbench;

public sealed class ProjectBoardWorkbenchTests
{
    [Fact]
    public void Root_snapshot_uses_project_cells()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        new DemoGraphSeed(factory).Seed();
        var snapshot = new WorkbenchReadStore(factory).GetSnapshot();

        Assert.Null(snapshot.Scope);
        Assert.Contains(snapshot.Cells, c => c.CellKind == "limbo");
        Assert.All(
            snapshot.Cells.Where(c => c.CellKind != "limbo"),
            c => Assert.Equal("project", c.CellKind));
        Assert.Contains(snapshot.Cells, c => c.Name.Contains("Harbor Court", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Project_board_keeps_relates_line_captures_off_the_board()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var deps = new TaskDependencyStore(factory);

        var parent = CreateTask(factory, ids.HarborProjectId, "MetroFiber");
        var line = CreateTask(factory, ids.HarborProjectId, "Clarify test task requirements");
        deps.Link(parent, line, TaskDependencyTypes.Relates, reason: "Captured as a line on the task board");

        var board = new WorkbenchReadStore(factory).GetSnapshot(ids.HarborProjectId);
        Assert.DoesNotContain(board.Cells, c => c.Id == line);
        var parentCell = Assert.Single(board.Cells, c => c.Id == parent);
        Assert.Contains(parentCell.Lines, l => l.TaskId == line);
    }

    [Fact]
    public void Project_board_uses_task_cells_with_dependency_lines()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var deps = new TaskDependencyStore(factory);

        var producer = CreateTask(factory, ids.HarborProjectId, "Confirm sprinkler count with inspector");
        var consumer = CreateTask(factory, ids.HarborProjectId, "Order sprinkler parts");
        deps.Link(producer, consumer, TaskDependencyTypes.Informs, expects: "count");

        var board = new WorkbenchReadStore(factory).GetSnapshot(ids.HarborProjectId);
        Assert.NotNull(board.Scope);
        Assert.Equal("project", board.Scope!.Kind);
        Assert.Equal(ids.HarborProjectId, board.Scope.ProjectId);
        Assert.All(board.Cells, c => Assert.Equal("task", c.CellKind));

        // informs edges still surface both tasks as cells (lines are related work, not nested captures).
        Assert.Contains(board.Cells, c => c.Id == producer);
        Assert.Contains(board.Cells, c => c.Id == consumer);

        var consumerCell = Assert.Single(board.Cells, c => c.Id == consumer);
        Assert.Contains(consumerCell.Lines, l => l.TaskId == producer);
    }

    [Fact]
    public void Unknown_project_throws()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        new DemoGraphSeed(factory).Seed();
        Assert.Throws<ArgumentException>(() => new WorkbenchReadStore(factory).GetSnapshot(Guid.NewGuid().ToString("D")));
    }

    private static string CreateTask(SqliteConnectionFactory factory, string projectId, string title)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tasks (id, project_id, title, status, created_at, updated_at)
            VALUES ($id, $project, $title, $status, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$status", TaskStatuses.NotStarted);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitProjectBoardTests", Guid.NewGuid().ToString("N"));

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

public sealed class TaskEmailThreadStoreTests
{
    [Fact]
    public void Link_is_idempotent_and_lists_for_task()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var taskId = CreateTask(factory, ids.HarborProjectId, "Open phone lines");
        SeedEmail(factory, "conv-1", "Line count reply", out var emailId);

        var store = new TaskEmailThreadStore(factory);
        var first = store.Link(taskId, "conv-1", emailId, actor: "user");
        var second = store.Link(taskId, "conv-1", emailId, actor: "user");
        Assert.Equal(first.Id, second.Id);

        var listed = Assert.Single(store.ListForTask(taskId));
        Assert.Equal("conv-1", listed.ConversationId);
        Assert.Equal("Line count reply", listed.Subject);
        Assert.Equal(1, listed.MessageCount);
    }

    private static void SeedEmail(
        SqliteConnectionFactory factory,
        string conversationId,
        string subject,
        out string emailId)
    {
        emailId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_artifacts (
              id, subject, sent_at, internet_message_id, body_preview, raw_path,
              created_at, updated_at, conversation_id, content_hash)
            VALUES (
              $id, $subject, $t, $mid, $preview, $raw,
              $t, $t, $conv, $hash);
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        cmd.Parameters.AddWithValue("$subject", subject);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$mid", $"<{emailId}@test>");
        cmd.Parameters.AddWithValue("$preview", "body");
        cmd.Parameters.AddWithValue("$raw", Path.Combine(Path.GetTempPath(), $"{emailId}.msg"));
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$hash", Guid.NewGuid().ToString("N"));
        cmd.ExecuteNonQuery();
    }

    private static string CreateTask(SqliteConnectionFactory factory, string projectId, string title)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tasks (id, project_id, title, status, created_at, updated_at)
            VALUES ($id, $project, $title, $status, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$status", TaskStatuses.NotStarted);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitEmailThreadTests", Guid.NewGuid().ToString("N"));

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
