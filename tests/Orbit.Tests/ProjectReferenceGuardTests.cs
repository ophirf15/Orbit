namespace Orbit.Tests;

public sealed class ProjectReferenceGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Theory]
    [InlineData("src/Orbit.Core/Orbit.Core.csproj")]
    [InlineData("src/Orbit.Infrastructure/Orbit.Infrastructure.csproj")]
    [InlineData("src/Orbit.Agent.Contracts/Orbit.Agent.Contracts.csproj")]
    [InlineData("src/Orbit.Core.Host/Orbit.Core.Host.csproj")]
    public void CoreLibraries_DoNotReferenceWinUiPackages(string relativeProjectPath)
    {
        var path = Path.Combine(RepoRoot, relativeProjectPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Missing project: {path}");

        var xml = File.ReadAllText(path);
        Assert.DoesNotContain("Microsoft.WindowsAppSDK", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UseWinUI", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.UI.Xaml", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoProjectReferencesFoundation()
    {
        var projects = Directory.GetFiles(RepoRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(projects);

        foreach (var project in projects)
        {
            var xml = File.ReadAllText(project);
            Assert.DoesNotContain("Foundation", xml, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"..\Foundation", xml, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbit.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate Orbit.sln from test base directory.");
    }
}
