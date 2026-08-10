using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;

namespace Orbit.Tests.Files;

public sealed class ProjectHomeFolderTests
{
    [Fact]
    public void SetHome_CreatesOrbitSandbox_AndIsOnlyHome()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        SeedProject(factory, out var projectId);

        var homeA = Path.Combine(temp.Root, "home-a");
        var homeB = Path.Combine(temp.Root, "home-b");
        Directory.CreateDirectory(homeA);
        Directory.CreateDirectory(homeB);
        File.WriteAllText(Path.Combine(homeA, "proposal.txt"), "Harbor Court proposal");

        var folders = new ProjectFolderStore(factory);
        var first = folders.SetHome(projectId, homeA);
        Assert.True(first.IsHome);
        Assert.True(Directory.Exists(OrbitHomeSandbox.GetSandboxRoot(homeA)));
        Assert.True(File.Exists(Path.Combine(OrbitHomeSandbox.GetSandboxRoot(homeA), "README.txt")));

        var second = folders.SetHome(projectId, homeB);
        Assert.True(second.IsHome);
        Assert.Equal(PathSafety.NormalizeFullPath(homeB), second.RootPath);

        var listed = folders.ListForProject(projectId);
        Assert.Equal(1, listed.Count(f => f.IsHome));
        Assert.Equal(second.Id, Assert.Single(listed, f => f.IsHome).Id);
        Assert.NotNull(folders.GetHome(projectId));
    }

    [Fact]
    public void PathSafety_AllowsWriteOnlyUnderHomeOrbitSandbox()
    {
        var home = Path.Combine(Path.GetTempPath(), "OrbitHomeAllow", Guid.NewGuid().ToString("N"));
        var generated = Path.Combine(Path.GetTempPath(), "OrbitGenAllow", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(generated);
        try
        {
            var sandbox = OrbitHomeSandbox.EnsureCreated(home);
            PathSafety.EnsureWritablePath(Path.Combine(sandbox, "draft.txt"), generated, [sandbox]);
            PathSafety.EnsureWritablePath(Path.Combine(generated, "out.txt"), generated, [sandbox]);
            Assert.Throws<UnauthorizedAccessException>(() =>
                PathSafety.EnsureWritablePath(Path.Combine(home, "proposal.txt"), generated, [sandbox]));
        }
        finally
        {
            TryDelete(home);
            TryDelete(generated);
        }
    }

    [Fact]
    public void Reindex_SkipsUnchangedFiles()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        SeedProject(factory, out var projectId);

        var folderRoot = Path.Combine(temp.Root, "files");
        Directory.CreateDirectory(folderRoot);
        File.WriteAllText(Path.Combine(folderRoot, "notes.txt"), "stable content for skip test");

        var folders = new ProjectFolderStore(factory);
        var external = new ExternalFileService(() => folders.ListActiveRootPaths());
        var index = new FileIndexService(factory, folders, external);
        var attached = folders.SetHome(projectId, folderRoot);

        var first = index.ReindexFolderDetailed(attached.Id);
        Assert.True(first.ExtractedCount >= 1);

        var second = index.ReindexFolderDetailed(attached.Id);
        Assert.True(second.SkippedUnchangedCount >= 1);
        Assert.Equal(0, second.ExtractedCount);
    }

    [Fact]
    public void PathSafety_EnsureWritablePath_DeniesHomeOutsideOrbit()
    {
        var home = Path.Combine(Path.GetTempPath(), "OrbitHomeDeny", Guid.NewGuid().ToString("N"));
        var generated = Path.Combine(Path.GetTempPath(), "OrbitGenDeny", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(generated);
        try
        {
            var sandbox = OrbitHomeSandbox.EnsureCreated(home);
            Assert.Throws<UnauthorizedAccessException>(() =>
                PathSafety.EnsureWritablePath(
                    Path.Combine(home, "secret.docx"),
                    generated,
                    [sandbox]));

            PathSafety.EnsureWritablePath(Path.Combine(sandbox, "ok.txt"), generated, [sandbox]);
        }
        finally
        {
            TryDelete(home);
            TryDelete(generated);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
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

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitHomeFolderTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "data", OrbitDbPaths.DatabaseFileName);

        public TempDb() => Directory.CreateDirectory(Path.Combine(Root, "data"));

        public void Dispose() => TryDelete(Root);
    }
}
