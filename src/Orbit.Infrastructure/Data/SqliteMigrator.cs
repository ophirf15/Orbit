using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Orbit.Infrastructure.Data;

public sealed class SqliteBackup
{
    public static string BackupDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Database file not found for backup.", databasePath);
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
        var backupPath = databasePath + $".bak-{stamp}";
        File.Copy(databasePath, backupPath, overwrite: false);
        return backupPath;
    }
}

public sealed class SqliteMigrator
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Assembly _migrationAssembly;
    private readonly string _resourcePrefix;

    public SqliteMigrator(SqliteConnectionFactory factory, Assembly? migrationAssembly = null)
    {
        _factory = factory;
        _migrationAssembly = migrationAssembly ?? typeof(SqliteMigrator).Assembly;
        _resourcePrefix = "Orbit.Infrastructure.Data.Migrations.";
    }

    public IReadOnlyList<string> ApplyPendingMigrations()
    {
        using var connection = _factory.CreateConnection();
        EnsureMigrationsTable(connection);

        var applied = GetAppliedVersions(connection);
        var pending = DiscoverMigrations()
            .Where(m => !applied.Contains(m.Version))
            .OrderBy(m => m.Version, StringComparer.Ordinal)
            .ToList();

        var newlyApplied = new List<string>();
        foreach (var migration in pending)
        {
            if (migration.IsDestructive && File.Exists(_factory.DatabasePath))
            {
                SqliteBackup.BackupDatabase(_factory.DatabasePath);
            }

            using var tx = connection.BeginTransaction();
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = migration.Sql;
                cmd.ExecuteNonQuery();
            }

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    "INSERT INTO schema_migrations (version, applied_at) VALUES ($v, $t);";
                insert.Parameters.AddWithValue("$v", migration.Version);
                insert.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }

            tx.Commit();
            newlyApplied.Add(migration.Version);
        }

        return newlyApplied;
    }

    public IReadOnlyList<string> GetAppliedVersions()
    {
        using var connection = _factory.CreateConnection();
        EnsureMigrationsTable(connection);
        return GetAppliedVersions(connection).OrderBy(v => v, StringComparer.Ordinal).ToList();
    }

    private static void EnsureMigrationsTable(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version TEXT NOT NULL PRIMARY KEY,
              applied_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static HashSet<string> GetAppliedVersions(SqliteConnection connection)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_migrations;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private IEnumerable<MigrationScript> DiscoverMigrations()
    {
        foreach (var name in _migrationAssembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(_resourcePrefix, StringComparison.Ordinal)
                || !name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = name[_resourcePrefix.Length..];
            var version = Path.GetFileNameWithoutExtension(file);
            using var stream = _migrationAssembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing resource {name}");
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            yield return new MigrationScript(
                version,
                sql,
                IsDestructive: version.Contains("destructive", StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed record MigrationScript(string Version, string Sql, bool IsDestructive);
}
