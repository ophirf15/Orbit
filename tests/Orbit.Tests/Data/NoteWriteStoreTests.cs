using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Tests.Data;

public sealed class NoteWriteStoreTests
{
    [Fact]
    public void AssignLimboToProject_CreatesTaskAndClearsLimbo()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var writer = new NoteWriteStore(factory);

        var limbo = writer.CreateCapture("Email: Vendor follow-up", projectId: null);
        Assert.True(limbo.IsLimbo);

        var assigned = writer.AssignLimboToProject(limbo.NoteId, ids.HarborProjectId);
        Assert.False(assigned.IsLimbo);
        Assert.Equal(ids.HarborProjectId, assigned.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(assigned.TaskId));

        var root = new WorkbenchReadStore(factory).GetSnapshot();
        Assert.DoesNotContain(root.Limbo, n => n.Id == limbo.NoteId);

        var board = new WorkbenchReadStore(factory).GetSnapshot(ids.HarborProjectId);
        Assert.Contains(board.Cells, c => c.Id == assigned.TaskId);
    }

    [Fact]
    public void CreateCapture_Limbo_PersistsAcrossReopen()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var writer = new NoteWriteStore(factory);

        var created = writer.CreateCapture("Call him back about proposal", projectId: null);
        Assert.True(created.IsLimbo);
        Assert.Null(created.TaskId);
        Assert.Equal("Call him back about proposal", created.OriginalText);

        var reader = new WorkbenchReadStore(new SqliteConnectionFactory(temp.DbPath));
        var snapshot = reader.GetSnapshot();
        Assert.Contains(snapshot.Limbo, n => n.Id == created.NoteId && n.OriginalText == created.OriginalText);
    }

    [Fact]
    public void CreateCapture_Project_CreatesNoteAndTask()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        var writer = new NoteWriteStore(factory);
        var created = writer.CreateCapture("Ping electrician", ids.HarborProjectId);

        Assert.False(created.IsLimbo);
        Assert.NotNull(created.TaskId);
        Assert.Equal(ids.HarborProjectId, created.ProjectId);

        var snapshot = new WorkbenchReadStore(factory).GetSnapshot();
        var cell = Assert.Single(snapshot.Cells, c => c.Id == ids.HarborProjectId);
        Assert.Contains(cell.Lines, l => l.TaskId == created.TaskId && l.Title == "Ping electrician");

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT original_text, project_id, is_limbo FROM notes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", created.NoteId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Ping electrician", reader.GetString(0));
        Assert.Equal(ids.HarborProjectId, reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    [Fact]
    public void CreateCapture_Whitespace_Throws()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var writer = new NoteWriteStore(factory);
        Assert.Throws<ArgumentException>(() => writer.CreateCapture("   ", null));
    }

    [Fact]
    public void Suggestion_DoesNotRewriteOriginalText()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        using var connection = factory.CreateConnection();
        using (var before = connection.CreateCommand())
        {
            before.CommandText = "SELECT original_text FROM notes WHERE id = $id;";
            before.Parameters.AddWithValue("$id", ids.LimboNoteId);
            Assert.Equal("Call him back about proposal", before.ExecuteScalar());
        }

        using (var suggestion = connection.CreateCommand())
        {
            suggestion.CommandText = "SELECT summary FROM agent_suggestions WHERE id = $id;";
            suggestion.Parameters.AddWithValue("$id", ids.LimboSuggestionId);
            Assert.Equal("Maybe assign to Harbor Court", suggestion.ExecuteScalar());
        }

        using (var after = connection.CreateCommand())
        {
            after.CommandText = "SELECT original_text FROM notes WHERE id = $id;";
            after.Parameters.AddWithValue("$id", ids.LimboNoteId);
            Assert.Equal("Call him back about proposal", after.ExecuteScalar());
        }

        var limbo = new WorkbenchReadStore(factory).GetSnapshot().Limbo;
        var note = Assert.Single(limbo, n => n.Id == ids.LimboNoteId);
        Assert.Equal("Call him back about proposal", note.OriginalText);
        Assert.Equal("Maybe assign to Harbor Court", note.SuggestionSummary);
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
            Path.Combine(Path.GetTempPath(), "OrbitNoteWriteTests", Guid.NewGuid().ToString("N"));

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
