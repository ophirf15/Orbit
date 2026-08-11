using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.Infrastructure.Data;

public sealed class ProjectRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Code { get; init; }
    public string? Summary { get; init; }
    public required string Status { get; init; }

    /// <summary>Workbench stripe hex (#RRGGBB), or null for theme default.</summary>
    public string? AccentColor { get; init; }

    public int SortOrder { get; init; }

    public double? BoardX { get; init; }

    public double? BoardY { get; init; }

    public double? BoardW { get; init; }

    public double? BoardH { get; init; }

    public ProjectDossier? Dossier { get; init; }

    public bool DossierEmpty { get; init; } = true;
}

public sealed class ProjectReadStore
{
    private readonly SqliteConnectionFactory _factory;

    public ProjectReadStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<ProjectRecord> ListActive()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code, summary, status, accent_color, sort_order, board_x, board_y, board_w, board_h, dossier_json
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY sort_order ASC, name COLLATE NOCASE;
            """;

        var list = new List<ProjectRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadProject(reader));
        }

        return list;
    }

    public ProjectRecord? Get(string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code, summary, status, accent_color, sort_order, board_x, board_y, board_w, board_h, dossier_json
            FROM projects
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", projectId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadProject(reader) : null;
    }

    private static ProjectRecord ReadProject(SqliteDataReader reader)
    {
        var dossierJson = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : null;
        var dossier = ProjectDossier.Parse(dossierJson);
        return new ProjectRecord
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Code = reader.IsDBNull(2) ? null : reader.GetString(2),
            Summary = reader.IsDBNull(3) ? null : reader.GetString(3),
            Status = reader.GetString(4),
            AccentColor = reader.IsDBNull(5) ? null : reader.GetString(5),
            SortOrder = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
            BoardX = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            BoardY = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            BoardW = reader.IsDBNull(9) ? null : reader.GetDouble(9),
            BoardH = reader.IsDBNull(10) ? null : reader.GetDouble(10),
            Dossier = dossier.IsStructurallyEmpty ? null : dossier,
            DossierEmpty = dossier.IsStructurallyEmpty,
        };
    }
}

public sealed class OrbitDatabase
{
    public SqliteConnectionFactory Factory { get; }
    public SqliteMigrator Migrator { get; }
    public string DatabasePath => Factory.DatabasePath;

    private OrbitDatabase(SqliteConnectionFactory factory)
    {
        Factory = factory;
        Migrator = new SqliteMigrator(factory);
    }

    public static OrbitDatabase Open(string localDataRoot)
    {
        var factory = SqliteConnectionFactory.FromLocalDataRoot(localDataRoot);
        var db = new OrbitDatabase(factory);
        db.Migrator.ApplyPendingMigrations();
        return db;
    }

    public DemoGraphIds SeedDemoIfEmpty()
    {
        var ids = new DemoGraphSeed(Factory).SeedIfEmpty();
        new SearchIndexRebuilder(Factory).Rebuild();
        return ids;
    }
}
