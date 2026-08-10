using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class OrbitMcpPublisherTests
{
    [Fact]
    public void SyncBundledIntoLocalAppData_CopiesExeAndPrefersExeLaunchPath()
    {
        var bundled = Path.Combine(Path.GetTempPath(), "orbit-mcp-bundled-" + Guid.NewGuid().ToString("N"));
        var publish = Path.Combine(Path.GetTempPath(), "orbit-mcp-publish-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("ORBIT_MCP_DIR");
        try
        {
            Directory.CreateDirectory(bundled);
            File.WriteAllText(Path.Combine(bundled, "Orbit.Mcp.exe"), "fake-exe");
            File.WriteAllText(Path.Combine(bundled, "Orbit.Mcp.dll"), "fake-dll");
            File.WriteAllText(Path.Combine(bundled, "Orbit.Mcp.deps.json"), "{}");

            Environment.SetEnvironmentVariable("ORBIT_MCP_DIR", publish);

            Assert.True(OrbitMcpPublisher.SyncBundledIntoLocalAppData(bundled));
            Assert.True(File.Exists(Path.Combine(publish, "Orbit.Mcp.exe")));
            Assert.True(File.Exists(Path.Combine(publish, "Orbit.Mcp.dll")));

            var launch = OrbitMcpPublisher.EnsurePublished();
            Assert.Equal(Path.Combine(publish, "Orbit.Mcp.exe"), launch);
            Assert.EndsWith(".exe", launch, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_MCP_DIR", previous);
            TryDelete(bundled);
            TryDelete(publish);
        }
    }

    [Fact]
    public void PreferLaunchable_FallsBackToDll_WhenExeMissing()
    {
        var publish = Path.Combine(Path.GetTempPath(), "orbit-mcp-dll-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("ORBIT_MCP_DIR");
        try
        {
            Directory.CreateDirectory(publish);
            var dll = Path.Combine(publish, "Orbit.Mcp.dll");
            File.WriteAllText(dll, "fake-dll");
            Environment.SetEnvironmentVariable("ORBIT_MCP_DIR", publish);

            var launch = OrbitMcpPublisher.PreferLaunchable(dll);
            Assert.Equal(dll, launch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORBIT_MCP_DIR", previous);
            TryDelete(publish);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
