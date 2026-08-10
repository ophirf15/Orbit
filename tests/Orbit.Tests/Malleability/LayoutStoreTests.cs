using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Malleability;

namespace Orbit.Tests.Malleability;

public sealed class LayoutStoreTests
{
    [Fact]
    public void Save_Apply_Revert_RestoresPriorSchema()
    {
        using var temp = new TempDb();
        var store = new LayoutStore(OpenMigrated(temp));

        var v1 = store.Save("Workbench lanes", """{"lanes":[{"id":"a"}]}""");
        Assert.Equal(1, v1.Version);
        Assert.False(v1.IsActive);

        var applied = store.Apply(v1.Id);
        Assert.True(applied.IsActive);

        var v2 = store.Save("Workbench lanes", """{"lanes":[{"id":"a"},{"id":"b"}]}""", layoutId: v1.Id);
        Assert.Equal(2, v2.Version);
        Assert.Contains("\"id\":\"b\"", v2.SchemaJson, StringComparison.Ordinal);

        var reverted = store.Revert(v1.Id);
        Assert.Equal(3, reverted.Version);
        Assert.DoesNotContain("\"id\":\"b\"", reverted.SchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"a\"", reverted.SchemaJson, StringComparison.Ordinal);

        var revisions = store.ListRevisions(v1.Id);
        Assert.True(revisions.Count >= 3);
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
            Path.Combine(Path.GetTempPath(), "OrbitLayoutTests", Guid.NewGuid().ToString("N"));

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
