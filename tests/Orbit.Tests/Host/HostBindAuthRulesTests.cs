using Orbit.Core.Host;

namespace Orbit.Tests.Host;

public sealed class HostBindAuthRulesTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void CanBind_Loopback_AllowsMissingKey(string bind)
    {
        Assert.True(PathSafety.CanBind(bind, null));
        Assert.True(PathSafety.CanBind(bind, " "));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.10")]
    [InlineData("10.0.0.5")]
    public void CanBind_NonLoopback_RequiresKey(string bind)
    {
        Assert.False(PathSafety.CanBind(bind, null));
        Assert.False(PathSafety.CanBind(bind, "   "));
        Assert.True(PathSafety.CanBind(bind, "orbit-secret"));
    }

    [Fact]
    public void IsLoopbackAddress_RejectsWildcard()
    {
        Assert.False(PathSafety.IsLoopbackAddress("0.0.0.0"));
        Assert.False(PathSafety.IsLoopbackAddress(""));
    }

    [Fact]
    public void AnonymousPath_IsHealthOnly()
    {
        Assert.True(HostEndpoints.IsAnonymousPath("/v1/health"));
        Assert.True(HostEndpoints.IsAnonymousPath("/v1/health/"));
        Assert.False(HostEndpoints.IsAnonymousPath("/v1/projects"));
        Assert.False(HostEndpoints.IsAnonymousPath("/v1/diagnostics"));
        Assert.False(HostEndpoints.IsAnonymousPath(null));
    }
}
