using Orbit.Core.Data;
using Orbit.Core.Pulse;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Tests.Workbench;

public sealed class WaitingOnDepthTests
{
    [Fact]
    public void SetWaitingOn_SeedsLabelFollowUpAndStatus()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projectId = new DemoGraphSeed(factory).Seed().HarborProjectId;
        var mutations = new OrbitMutationStore(factory);
        var taskId = CreateTask(factory, projectId, "Grant signed PMA");

        var result = mutations.SetWaitingOn(
            taskId,
            waitingOnLabel: "Grant",
            followUpAt: "2026-08-15",
            cadence: "3d",
            actor: "user");

        Assert.Equal(TaskStatuses.Waiting, result.Status);
        Assert.Equal("Grant", result.WaitingOnLabel);
        Assert.Equal("2026-08-15", result.FollowUpAt);
        Assert.Equal("3d", result.Cadence);
        Assert.Null(result.SatisfiedAt);

        var loaded = new ProjectContextReadStore(factory).GetTask(taskId);
        Assert.NotNull(loaded);
        Assert.Equal("Grant", loaded!.WaitingOnLabel);
        Assert.Equal(TaskStatuses.Waiting, loaded.Status);
    }

    [Fact]
    public void ClearWaitingOn_RequiresEvidence_AndPreservesLabel()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projectId = new DemoGraphSeed(factory).Seed().HarborProjectId;
        var mutations = new OrbitMutationStore(factory);
        var taskId = CreateTask(factory, projectId, "Grant signed PMA");
        mutations.SetWaitingOn(taskId, waitingOnLabel: "Grant", followUpAt: "2026-08-10", actor: "user");

        Assert.Throws<ArgumentException>(() => mutations.ClearWaitingOn(taskId, evidenceRef: "  "));

        var cleared = mutations.ClearWaitingOn(taskId, evidenceRef: "note: signed PMA received", actor: "user");
        Assert.Equal("Grant", cleared.WaitingOnLabel);
        Assert.Equal("note: signed PMA received", cleared.EvidenceRef);
        Assert.NotNull(cleared.SatisfiedAt);
        Assert.Equal(TaskStatuses.Active, cleared.Status);

        var loaded = new ProjectContextReadStore(factory).GetTask(taskId)!;
        Assert.Equal("Grant", loaded.WaitingOnLabel);
        Assert.Equal("2026-08-10", loaded.WaitingFollowUpAt);
        Assert.NotNull(loaded.WaitingSatisfiedAt);
        Assert.Equal("note: signed PMA received", loaded.WaitingEvidenceRef);
        Assert.Equal(TaskStatuses.Active, loaded.Status);
    }

    [Fact]
    public void SatisfyDependency_MarksSatisfiedWithoutDeletingEdgeOrExpects()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projectId = new DemoGraphSeed(factory).Seed().HarborProjectId;
        var deps = new TaskDependencyStore(factory);
        var producer = CreateTask(factory, projectId, "Grant returns PMA");
        var consumer = CreateTask(factory, projectId, "File PMA package");
        var edge = deps.Link(
            producer,
            consumer,
            TaskDependencyTypes.Informs,
            expects: "signed PMA",
            followUpAt: "2026-08-09",
            actor: "user");

        var satisfied = deps.Satisfy(edge.Id, "email:thread-42", actor: "user");
        Assert.NotNull(satisfied.SatisfiedAt);
        Assert.Equal("email:thread-42", satisfied.EvidenceRef);
        Assert.Equal("signed PMA", satisfied.Expects);
        Assert.Equal("2026-08-09", satisfied.FollowUpAt);

        var waiting = Assert.Single(deps.ListForTask(consumer), e => e.AnchorIsSuccessor);
        Assert.True(waiting.IsSatisfied);
        Assert.Equal("signed PMA", waiting.Dependency.Expects);
        Assert.Equal(1, CountDependencies(factory));
    }

    [Fact]
    public void AttentionReason_FollowUpOverdue_ReturnsFollowUpDue()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Chase Grant",
            sourceKind: null,
            updatedAt: now.AddHours(-6).ToString("o"),
            now: now,
            waitingFollowUpAt: "2026-08-10");

        Assert.Equal("Follow-up due", label);
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
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
            Path.Combine(Path.GetTempPath(), "OrbitWaitingOnDepthTests", Guid.NewGuid().ToString("N"));

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
