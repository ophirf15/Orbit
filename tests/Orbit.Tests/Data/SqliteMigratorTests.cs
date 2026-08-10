using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Data;

public sealed class SqliteMigratorTests
{
    [Fact]
    public void Migrate_EmptyDatabase_CreatesGraphTables()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var migrator = new SqliteMigrator(factory);

        var applied = migrator.ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0001_", StringComparison.Ordinal));

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'relationships';";
        Assert.Equal("relationships", cmd.ExecuteScalar());

        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'tasks';";
        Assert.Equal("tasks", cmd.ExecuteScalar());

        Assert.Equal(OrbitDbPaths.DatabaseFileName, Path.GetFileName(factory.DatabasePath));
    }

    [Fact]
    public void Migrate_Twice_IsIdempotent()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var migrator = new SqliteMigrator(factory);

        var first = migrator.ApplyPendingMigrations();
        var second = migrator.ApplyPendingMigrations();

        Assert.NotEmpty(first);
        Assert.Empty(second);
        Assert.NotEmpty(migrator.GetAppliedVersions());
    }

    [Fact]
    public void Backup_CreatesSiblingFile()
    {
        using var temp = new TempDb();
        File.WriteAllText(temp.DbPath, "not-really-sqlite-but-fine-for-copy");
        var backup = SqliteBackup.BackupDatabase(temp.DbPath);
        Assert.True(File.Exists(backup));
        Assert.StartsWith(temp.DbPath + ".bak-", backup, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitMigrateTests", Guid.NewGuid().ToString("N"));

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
