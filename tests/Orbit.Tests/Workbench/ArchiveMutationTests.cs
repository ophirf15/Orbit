using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Tests.Workbench;

public sealed class ArchiveMutationTests
{
    [Fact]
    public void Archive_task_removes_from_workbench_lines()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var mutations = new OrbitMutationStore(factory);
        var workbench = new WorkbenchReadStore(factory);

        var capture = notes.CreateCapture("Order MetroFiber", ids.HarborProjectId);
        Assert.False(string.IsNullOrWhiteSpace(capture.TaskId));
        Assert.Contains(workbench.GetSnapshot().Cells.SelectMany(c => c.Lines), l => l.TaskId == capture.TaskId);

        mutations.Archive("task", capture.TaskId!);
        Assert.DoesNotContain(workbench.GetSnapshot().Cells.SelectMany(c => c.Lines), l => l.TaskId == capture.TaskId);
    }

    [Fact]
    public void Archive_project_hides_cell()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var mutations = new OrbitMutationStore(factory);
        var workbench = new WorkbenchReadStore(factory);

        notes.CreateCapture("line", ids.HarborProjectId);
        Assert.Contains(workbench.GetSnapshot().Cells, c => c.Id == ids.HarborProjectId);

        mutations.Archive("project", ids.HarborProjectId);
        Assert.DoesNotContain(workbench.GetSnapshot().Cells, c => c.Id == ids.HarborProjectId);
    }

    [Fact]
    public void Archive_blocker_hides_from_project_context()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var mutations = new OrbitMutationStore(factory);
        var contexts = new ProjectContextReadStore(factory);

        var before = contexts.GetContext(ids.HarborProjectId);
        Assert.NotNull(before);
        Assert.NotEmpty(before!.Blockers);
        var blockerId = before.Blockers[0].Id;
        Assert.False(string.IsNullOrWhiteSpace(before.Blockers[0].CreatedAt));

        mutations.Archive("blocker", blockerId);

        var after = contexts.GetContext(ids.HarborProjectId);
        Assert.NotNull(after);
        Assert.DoesNotContain(after!.Blockers, b => b.Id == blockerId);
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
            Path.Combine(Path.GetTempPath(), "OrbitArchiveTests", Guid.NewGuid().ToString("N"));

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
