using Orbit.Core.Data;
using Orbit.Infrastructure.Capture;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Workbench;

public sealed class CapturePreviewAssemblerTests
{
    [Fact]
    public void Assemble_MatchesAlias_AndExposesReasonLabel()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var created = projects.Create("Acme Widget Co");
        projects.AddAlias(created.Id, "Widget");

        var assembler = new CapturePreviewAssembler(factory);
        var result = assembler.Assemble("Please schedule Widget install — waiting on Grant");

        Assert.NotNull(result.MatchedProject);
        Assert.Equal(created.Id, result.MatchedProject!.ProjectId);
        Assert.Equal("alias", result.MatchedProject.Reason);
        Assert.Equal("Matched via alias", result.MatchedProject.ReasonLabel);
        Assert.True(result.MatchedProject.AutoSelected);
        Assert.Contains("Grant", result.WaitingOnHint ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Please schedule Widget install — waiting on Grant", result.OriginalText.Trim());
    }

    [Fact]
    public void Assemble_ScopedDefault_UsesScopedReasonWhenNoStrongerMatch()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var scoped = projects.Create("Harbor Court");
        _ = projects.Create("Unrelated Site");

        var assembler = new CapturePreviewAssembler(factory);
        var result = assembler.Assemble("Order more printer paper", defaultProjectId: scoped.Id);

        Assert.NotNull(result.MatchedProject);
        Assert.Equal(scoped.Id, result.MatchedProject!.ProjectId);
        Assert.Equal("scoped", result.MatchedProject.Reason);
        Assert.Equal("Scoped project", result.MatchedProject.ReasonLabel);
    }

    [Fact]
    public void Assemble_EmptyText_NoInventedFields()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var assembler = new CapturePreviewAssembler(factory);
        var result = assembler.Assemble("   ");

        Assert.True(string.IsNullOrWhiteSpace(result.Title));
        Assert.Null(result.MatchedProject);
        Assert.Empty(result.Candidates);
        Assert.Null(result.PeopleHint);
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
            Path.Combine(Path.GetTempPath(), "OrbitCapturePreviewTests", Guid.NewGuid().ToString("N"));

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
