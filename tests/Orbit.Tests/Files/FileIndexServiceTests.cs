using System.Reflection;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;

namespace Orbit.Tests.Files;

public sealed class ExternalFileCapabilityShapeTests
{
    [Fact]
    public void Interface_HasNoMutationMembers()
    {
        var names = typeof(IExternalFileCapability)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(IExternalFileCapability.List), names);
        Assert.Contains(nameof(IExternalFileCapability.Stat), names);
        Assert.Contains(nameof(IExternalFileCapability.OpenRead), names);
        Assert.Contains(nameof(IExternalFileCapability.ReadTextPreview), names);
        Assert.Contains(nameof(IExternalFileCapability.OpenExternally), names);

        Assert.DoesNotContain("Write", names);
        Assert.DoesNotContain("Delete", names);
        Assert.DoesNotContain("Rename", names);
        Assert.DoesNotContain("Move", names);
        Assert.DoesNotContain("Overwrite", names);
        Assert.DoesNotContain("Create", names);
    }
}

public sealed class FileIndexServiceTests
{
    [Fact]
    public void AttachAndIndex_FindsFileByContent()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        SeedProject(factory, out var projectId);

        var folderRoot = Path.Combine(temp.Root, "project-files");
        Directory.CreateDirectory(folderRoot);
        File.WriteAllText(Path.Combine(folderRoot, "notes.txt"), "Invoice for Harbor Court fiber install");
        File.WriteAllText(Path.Combine(folderRoot, "W-9-Acme.txt"), "Form W-9 Request for Taxpayer Identification Number");

        var folders = new ProjectFolderStore(factory);
        var external = new ExternalFileService(() => folders.ListActiveRootPaths());
        var index = new FileIndexService(factory, folders, external);

        var attached = folders.Attach(projectId, folderRoot);
        var count = index.ReindexFolder(attached.Id);
        Assert.True(count >= 2);

        var hits = index.Search("fiber", projectId);
        Assert.Contains(hits, h => h.DisplayName.Contains("notes", StringComparison.OrdinalIgnoreCase));

        var w9 = index.Search("W-9", projectId);
        var w9Hit = Assert.Single(w9, h => h.DisplayName.Contains("W-9", StringComparison.OrdinalIgnoreCase));
        index.LinkToProject(w9Hit.Id, projectId);
        index.LinkToEntity(w9Hit.Id, "organization", Guid.NewGuid().ToString("D"));
        var links = index.ListLinks(w9Hit.Id);
        Assert.Contains(links, l => l.EntityType == "project");
        Assert.Contains(links, l => l.EntityType == "organization");
    }

    [Fact]
    public void AttachAndIndex_ListsFilesWithoutSearchQuery()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        SeedProject(factory, out var projectId);

        var folderRoot = Path.Combine(temp.Root, "project-files");
        Directory.CreateDirectory(folderRoot);
        File.WriteAllText(Path.Combine(folderRoot, "a.txt"), "alpha");
        File.WriteAllText(Path.Combine(folderRoot, "b.txt"), "beta");

        var folders = new ProjectFolderStore(factory);
        var external = new ExternalFileService(() => folders.ListActiveRootPaths());
        var index = new FileIndexService(factory, folders, external);
        var attached = folders.Attach(projectId, folderRoot);
        index.ReindexFolder(attached.Id);

        var listed = index.ListForProject(projectId);
        Assert.True(listed.Count >= 2);
    }

    [Fact]
    public void ExternalService_DeniesPathOutsideRoots()
    {
        using var temp = new TempDb();
        var root = Path.Combine(temp.Root, "allowed");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(temp.Root, "secret.txt");
        File.WriteAllText(outside, "nope");

        var external = new ExternalFileService(() => [root]);
        Assert.Throws<UnauthorizedAccessException>(() => external.Stat(outside));
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var applied = new SqliteMigrator(factory).ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0001_", StringComparison.Ordinal));
        Assert.Contains(applied, v => v.StartsWith("0002_", StringComparison.Ordinal));
        return factory;
    }

    private static void SeedProject(SqliteConnectionFactory factory, out string projectId)
    {
        projectId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, 'Harbor Court', 'active', $t, $t);";
        cmd.Parameters.AddWithValue("$id", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitFileIndexTests", Guid.NewGuid().ToString("N"));

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
