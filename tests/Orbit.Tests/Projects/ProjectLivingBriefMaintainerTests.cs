using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Projects;

public sealed class ProjectLivingBriefMaintainerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "OrbitLivingBriefTests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly ProjectWriteStore _projects;
    private readonly ProjectContextReadStore _contexts;
    private readonly ProjectLivingBriefMaintainer _maintainer;
    private readonly OrbitMutationStore _mutations;

    public ProjectLivingBriefMaintainerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(Path.Combine(_root, "data")));
        new SqliteMigrator(_factory).ApplyPendingMigrations();
        _projects = new ProjectWriteStore(_factory);
        _contexts = new ProjectContextReadStore(_factory);
        _maintainer = new ProjectLivingBriefMaintainer(_contexts, _projects);
        _mutations = new OrbitMutationStore(_factory);
    }

    [Fact]
    public void EnsureBaseline_WritesSummaryAndPriorities_WhenBlank()
    {
        var created = _projects.Create("Harbor Court");
        _mutations.CreateTask("Close roof bid", created.Id, TaskStatuses.Active, "test", nextAction: "Send final numbers");

        var result = _maintainer.EnsureBaseline(created.Id);

        Assert.True(result.Applied);
        Assert.True(result.SummaryUpdated);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.Contains("Objective:", result.Summary, StringComparison.Ordinal);

        var ctx = _contexts.GetContext(created.Id);
        Assert.NotNull(ctx);
        Assert.Equal(result.Summary, ctx!.Summary);
        Assert.False(string.IsNullOrWhiteSpace(ctx.Summary));
    }

    [Fact]
    public void EnsureBaseline_DoesNotWipeOperatorSummary()
    {
        var created = _projects.Create("Harbor Court", summary: "Keep my words.");
        _mutations.CreateTask("Close roof bid", created.Id, TaskStatuses.Active, "test", nextAction: "Send final numbers");

        var result = _maintainer.EnsureBaseline(created.Id);

        Assert.Equal("Keep my words.", _contexts.GetContext(created.Id)!.Summary);
        Assert.False(result.SummaryUpdated);
    }

    [Fact]
    public void Refresh_AppendsAutoBrief_WithoutRemovingOperatorProse()
    {
        var created = _projects.Create("Harbor Court", summary: "Operator owned prose.");
        _projects.UpdateDossier(created.Id, new ProjectDossierPatch
        {
            CurrentPriorities = ["Existing priority"],
            Phase = "Construction",
        });
        _mutations.CreateTask("Chase CO", created.Id, TaskStatuses.Active, "test", nextAction: "Call city");

        var result = _maintainer.Refresh(created.Id);

        Assert.True(result.Applied);
        Assert.True(result.SummaryUpdated);
        Assert.StartsWith("Operator owned prose.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Auto brief (", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Call city", result.Summary, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }
}
