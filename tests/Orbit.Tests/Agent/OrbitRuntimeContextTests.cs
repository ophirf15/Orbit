using Orbit.Core.Agent;

namespace Orbit.Tests.Agent;

public sealed class OrbitRuntimeContextTests
{
    [Fact]
    public void ToSystemPrompt_IncludesRouteAndTools()
    {
        var ctx = new OrbitRuntimeContext
        {
            Route = "nav.workbench",
            ProjectName = "The Harbor Court",
            WorkbenchProjectNames = ["The Harbor Court", "Riverview"],
        };

        var prompt = ctx.ToSystemPrompt();
        Assert.Contains("nav.workbench", prompt, StringComparison.Ordinal);
        Assert.Contains("The Harbor Court", prompt, StringComparison.Ordinal);
        Assert.Contains("orbit_get_related_context", prompt, StringComparison.Ordinal);
        Assert.Contains("Live Orbit runtime context", prompt, StringComparison.Ordinal);
    }
}
