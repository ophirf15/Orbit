using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Malleability;

namespace Orbit.Tests.Malleability;

public sealed class CustomFieldStoreTests
{
    [Fact]
    public void AddField_ThenSetValue_RoundTrips()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var store = new CustomFieldStore(factory);

        var field = store.AddField(
            "workstream",
            "utility_account_number",
            "text",
            validationJson: """{"maxLength":64}""",
            displayJson: """{"label":"Utility account #"}""");

        Assert.Equal("workstream", field.EntityType);
        Assert.Equal("utility_account_number", field.Key);
        Assert.Equal("text", field.FieldType);

        using var valueDoc = JsonDocument.Parse("\"ACC-99\"");
        var value = store.SetValue("workstream", Guid.NewGuid().ToString("D"), "utility_account_number", valueDoc.RootElement);
        Assert.Contains("ACC-99", value.ValueJson, StringComparison.Ordinal);

        var listed = store.ListDefinitions("workstream");
        Assert.Contains(listed, f => f.Id == field.Id);
    }

    [Fact]
    public void SetValue_InvalidChoice_Throws()
    {
        using var temp = new TempDb();
        var store = new CustomFieldStore(OpenMigrated(temp));
        store.AddField(
            "project",
            "tier",
            "choice",
            validationJson: """{"choices":["a","b"]}""");

        using var valueDoc = JsonDocument.Parse("\"c\"");
        Assert.Throws<ArgumentException>(() =>
            store.SetValue("project", "p1", "tier", valueDoc.RootElement));
    }

    [Fact]
    public void Migration_0008_CreatesMalleabilityTables()
    {
        using var temp = new TempDb();
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var applied = new SqliteMigrator(factory).ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0008_", StringComparison.Ordinal));

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='layout_revisions';";
        Assert.Equal("layout_revisions", cmd.ExecuteScalar());
        cmd.CommandText = "PRAGMA table_info(custom_fields);";
        using var reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("validation_json", columns);
        Assert.Contains("display_json", columns);
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
            Path.Combine(Path.GetTempPath(), "OrbitCustomFieldTests", Guid.NewGuid().ToString("N"));

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
