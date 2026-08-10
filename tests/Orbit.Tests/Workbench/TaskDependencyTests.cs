using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Workbench;

public sealed class TaskDependencyTests
{
    [Fact]
    public void Link_is_idempotent_and_enriches_existing_edge()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var store = new TaskDependencyStore(factory);
        var producer = CreateTask(factory, projectId, "Confirm phone line count with vendor");
        var consumer = CreateTask(factory, projectId, "Open phone lines with carrier");

        var first = store.Link(producer, consumer, TaskDependencyTypes.Informs, actor: "user");
        var second = store.Link(
            producer,
            consumer,
            TaskDependencyTypes.Informs,
            reason: "needs the count",
            expects: "line count",
            actor: "user");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("line count", second.Expects);
        Assert.Equal("needs the count", second.Reason);
        Assert.Equal(1, CountDependencies(factory));
    }

    [Fact]
    public void Link_rejects_self_and_cycles()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var store = new TaskDependencyStore(factory);
        var a = CreateTask(factory, projectId, "Task A");
        var b = CreateTask(factory, projectId, "Task B");
        var c = CreateTask(factory, projectId, "Task C");

        Assert.Throws<ArgumentException>(() => store.Link(a, a));

        store.Link(a, b, TaskDependencyTypes.Blocks);
        store.Link(b, c, TaskDependencyTypes.Blocks);

        // c → a would close the loop a → b → c → a.
        Assert.Throws<ArgumentException>(() => store.Link(c, a, TaskDependencyTypes.Blocks));
    }

    [Fact]
    public void ListForTask_separates_waiting_on_from_feeds()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var store = new TaskDependencyStore(factory);
        var upstream = CreateTask(factory, projectId, "Get vendor list");
        var middle = CreateTask(factory, projectId, "Open accounts");
        var downstream = CreateTask(factory, projectId, "Schedule install");

        store.Link(upstream, middle, TaskDependencyTypes.Informs, expects: "vendor list");
        store.Link(middle, downstream, TaskDependencyTypes.Blocks);

        var edges = store.ListForTask(middle);
        Assert.Equal(2, edges.Count);

        var waitingOn = Assert.Single(edges, e => e.AnchorIsSuccessor);
        Assert.Equal(upstream, waitingOn.OtherTaskId);
        Assert.Equal("vendor list", waitingOn.Dependency.Expects);

        var feeds = Assert.Single(edges, e => !e.AnchorIsSuccessor);
        Assert.Equal(downstream, feeds.OtherTaskId);
    }

    [Fact]
    public void Unlink_removes_edge_and_allows_relink()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var store = new TaskDependencyStore(factory);
        var a = CreateTask(factory, projectId, "Task A");
        var b = CreateTask(factory, projectId, "Task B");

        var edge = store.Link(a, b, TaskDependencyTypes.Blocks);
        Assert.True(store.Unlink(edge.Id, actor: "user"));
        Assert.False(store.Unlink(edge.Id, actor: "user"));
        Assert.Equal(0, CountDependencies(factory));

        var relinked = store.Link(a, b, TaskDependencyTypes.Blocks);
        Assert.NotEqual(edge.Id, relinked.Id);
    }

    [Fact]
    public void ListReadyDependencies_surfaces_completed_predecessors_only()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var store = new TaskDependencyStore(factory);
        var producer = CreateTask(factory, projectId, "Confirm line count");
        var consumer = CreateTask(factory, projectId, "Open phone lines");
        store.Link(producer, consumer, TaskDependencyTypes.Informs, expects: "line count");

        Assert.Empty(store.ListReadyDependencies());

        SetTaskStatus(factory, producer, TaskStatuses.Complete);
        var ready = Assert.Single(store.ListReadyDependencies());
        Assert.Equal(consumer, ready.Dependency.SuccessorTaskId);
        Assert.Equal("line count", ready.Dependency.Expects);

        SetTaskStatus(factory, consumer, TaskStatuses.Complete);
        Assert.Empty(store.ListReadyDependencies());
    }

    [Fact]
    public void AcceptLinkTasks_creates_dependency()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var suggestions = new SuggestionStore(factory);
        var producer = CreateTask(factory, projectId, "Confirm line count with vendor");
        var consumer = CreateTask(factory, projectId, "Open phone lines with carrier");

        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.LinkTasks,
            Summary = "Link these?",
            ProjectId = projectId,
            TaskId = consumer,
            PayloadJson = JsonSerializer.Serialize(new
            {
                predecessorTaskId = producer,
                successorTaskId = consumer,
                dependencyType = TaskDependencyTypes.Informs,
                expects = "line count",
            }),
        });

        var accepted = suggestions.Accept(suggestion.Id, actor: "user");
        Assert.Equal(SuggestionStatuses.Accepted, accepted.Suggestion.Status);
        Assert.NotNull(accepted.CreatedDependencyId);

        var edge = Assert.Single(new TaskDependencyStore(factory).ListForTask(consumer));
        Assert.True(edge.AnchorIsSuccessor);
        Assert.Equal(producer, edge.OtherTaskId);
        Assert.Equal("line count", edge.Dependency.Expects);
    }

    [Fact]
    public void AcceptMergeIntoTask_appends_without_destroying_existing_notes()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var suggestions = new SuggestionStore(factory);
        var taskId = CreateTask(factory, projectId, "Open phone lines", body: "Existing notes from me");

        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Email may answer this",
            ProjectId = projectId,
            TaskId = taskId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                taskId,
                text = "We need 12 lines at the property",
                field = "body",
                sourceType = "email",
                sourceId = "email-1",
            }),
        });

        var accepted = suggestions.Accept(suggestion.Id, actor: "user");
        Assert.Equal(taskId, accepted.AppliedTaskId);

        var body = ReadTaskBody(factory, taskId);
        Assert.Contains("Existing notes from me", body, StringComparison.Ordinal);
        Assert.Contains("We need 12 lines at the property", body, StringComparison.Ordinal);
        Assert.Contains("From email", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_proposes_informs_link_between_producer_and_consumer()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var suggestions = new SuggestionStore(factory);
        var engine = new TaskRelationshipEngine(factory, suggestions);

        var producer = CreateTask(factory, projectId, "Confirm phone lines needed with Pyrocomm vendor");
        var consumer = CreateTask(factory, projectId, "Open phone lines account with MetroFiber");

        var created = engine.SuggestLinksForTask(consumer);
        var link = Assert.Single(created, s => s.SuggestionType == SuggestionTypes.LinkTasks);

        using var doc = JsonDocument.Parse(link.PayloadJson!);
        Assert.Equal(producer, doc.RootElement.GetProperty("predecessorTaskId").GetString());
        Assert.Equal(consumer, doc.RootElement.GetProperty("successorTaskId").GetString());
        Assert.Equal(TaskDependencyTypes.Informs, doc.RootElement.GetProperty("dependencyType").GetString());

        // Re-running must not duplicate the proposal.
        Assert.Empty(engine.SuggestLinksForTask(consumer));
    }

    [Fact]
    public void Engine_proposes_ready_confirmation_when_gating_predecessor_completes()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var (projectId, _) = SeedProject(factory);
        var suggestions = new SuggestionStore(factory);
        var engine = new TaskRelationshipEngine(factory, suggestions);
        var store = new TaskDependencyStore(factory);

        var producer = CreateTask(factory, projectId, "Confirm line count");
        var consumer = CreateTask(factory, projectId, "Open phone lines");
        store.Link(producer, consumer, TaskDependencyTypes.Informs, expects: "line count");
        SetTaskStatus(factory, producer, TaskStatuses.Complete, nextAction: "12 lines confirmed");

        var ready = Assert.Single(engine.SuggestReadyDependencies());
        Assert.Equal(SuggestionTypes.DependencyReady, ready.SuggestionType);
        Assert.Equal(consumer, ready.TaskId);

        Assert.Empty(engine.SuggestReadyDependencies());
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private static (string ProjectId, DemoGraphIds Ids) SeedProject(SqliteConnectionFactory factory)
    {
        var ids = new DemoGraphSeed(factory).Seed();
        return (ids.HarborProjectId, ids);
    }

    private static string CreateTask(
        SqliteConnectionFactory factory,
        string projectId,
        string title,
        string? body = null)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tasks (id, project_id, title, body, status, created_at, updated_at)
            VALUES ($id, $project, $title, $body, $status, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", TaskStatuses.NotStarted);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static void SetTaskStatus(
        SqliteConnectionFactory factory,
        string taskId,
        string status,
        string? nextAction = null)
    {
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE tasks
            SET status = $status,
                next_action = COALESCE($next, next_action),
                updated_at = $t
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", (object?)nextAction ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.ExecuteNonQuery();
    }

    private static string ReadTaskBody(SqliteConnectionFactory factory, string taskId)
    {
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(body, '') FROM tasks WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", taskId);
        return (string)cmd.ExecuteScalar()!;
    }

    private static int CountDependencies(SqliteConnectionFactory factory)
    {
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM task_dependencies;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitTaskDependencyTests", Guid.NewGuid().ToString("N"));

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
                // best-effort
            }
        }
    }
}
