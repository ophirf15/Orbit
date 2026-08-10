namespace Orbit.IntegrationTests;

public sealed class SmokePlaceholders
{
    [Fact]
    public void Solution_Layout_Exists()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Orbit.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        Assert.True(Directory.Exists(Path.Combine(dir!.FullName, "src", "Orbit.App")));
        Assert.True(Directory.Exists(Path.Combine(dir.FullName, "src", "Orbit.Core.Host")));
    }
}
