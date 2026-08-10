using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Workbench;

public sealed class ProjectCreateAndLimboCellTests
{
    [Fact]
    public void ProjectWriteStore_Create_InsertsActiveProject()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var store = new ProjectWriteStore(factory);

        var created = store.Create("  Acme Retrofit  ", "from test");
        Assert.False(string.IsNullOrWhiteSpace(created.Id));
        Assert.Equal("Acme Retrofit", created.Name);
        Assert.Equal("from test", created.Summary);
        Assert.Equal("active", created.Status);

        var blank = store.Create("   ");
        Assert.Equal("Untitled project", blank.Name);
    }

    [Fact]
    public void ProjectNaming_FromFolderPath_UsesLastSegment()
    {
        Assert.Equal("Harbor Court", ProjectNaming.FromFolderPath(@"C:\Clients\Harbor Court"));
        Assert.Equal("Harbor Court", ProjectNaming.FromFolderPath(@"C:\Clients\Harbor Court\"));
        Assert.Equal("Untitled project", ProjectNaming.FromFolderPath("   "));
    }

    [Fact]
    public void Root_snapshot_includes_limbo_cell_with_note_lines()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        SeedProject(factory, out _);
        SeedLimboNote(factory, "Park this email claim");

        var snapshot = new WorkbenchReadStore(factory).GetSnapshot();
        Assert.Null(snapshot.Scope);

        var limbo = Assert.Single(snapshot.Cells, c => c.CellKind == WorkbenchCellKinds.Limbo);
        Assert.Equal(WorkbenchCellKinds.LimboEntityId, limbo.Id);
        Assert.Equal("Limbo", limbo.Name);
        Assert.Contains(limbo.Lines, l => l.Title.Contains("Park this email", StringComparison.Ordinal));
        Assert.All(snapshot.Cells.Where(c => c.CellKind != WorkbenchCellKinds.Limbo),
            c => Assert.Equal(WorkbenchCellKinds.Project, c.CellKind));
        Assert.NotEmpty(snapshot.Limbo);
    }

    [Fact]
    public void WorkbenchLayoutStore_PersistsLimboLayout()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var layouts = new WorkbenchLayoutStore(factory);

        var saved = layouts.SetLayout(
            WorkbenchCellKinds.Limbo,
            WorkbenchCellKinds.LimboEntityId,
            x: 16,
            y: 32,
            width: 280,
            height: 220,
            sortOrder: 0);

        Assert.Equal(WorkbenchCellKinds.Limbo, saved.EntityKind);
        var loaded = layouts.TryGetSyntheticLayout(WorkbenchCellKinds.LimboEntityId);
        Assert.NotNull(loaded);
        Assert.Equal(16, loaded!.X);
        Assert.Equal(32, loaded.Y);

        var snapshot = new WorkbenchReadStore(factory).GetSnapshot();
        var limbo = Assert.Single(snapshot.Cells, c =>
            string.Equals(c.CellKind, WorkbenchCellKinds.Limbo, StringComparison.Ordinal));
        Assert.Equal(16, limbo.BoardX);
        Assert.Equal(32, limbo.BoardY);
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private static void SeedProject(SqliteConnectionFactory factory, out string projectId)
    {
        projectId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, 'North Pier', 'active', $t, $t);";
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static void SeedLimboNote(SqliteConnectionFactory factory, string text)
    {
        var id = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO notes (id, original_text, is_limbo, created_at, updated_at)
            VALUES ($id, $text, 1, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitCreateLimboTests", Guid.NewGuid().ToString("N"));

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
