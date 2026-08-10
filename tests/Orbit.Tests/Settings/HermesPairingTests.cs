using Orbit.Core.Settings;

namespace Orbit.Tests.Settings;

public sealed class HermesPairingTests
{
    [Fact]
    public void GenerateApiServerKey_Is64Hex()
    {
        var key = HermesPairing.GenerateApiServerKey();
        Assert.Equal(64, key.Length);
        Assert.Matches("^[0-9a-f]{64}$", key);
    }

    [Fact]
    public void BuildReachableCoreUrl_RewritesHostFromLanBind()
    {
        var url = HermesPairing.BuildReachableCoreUrl("192.168.1.50", "http://127.0.0.1:8741");
        Assert.Equal("http://192.168.1.50:8741", url);
    }

    [Fact]
    public void BuildReachableCoreUrl_KeepsLoopbackWhenBindLoopback()
    {
        var url = HermesPairing.BuildReachableCoreUrl("127.0.0.1", "http://127.0.0.1:8741");
        Assert.Equal("http://127.0.0.1:8741", url);
    }

    [Fact]
    public void DeriveDashboardUrl_SwapsApiPortForDashboard()
    {
        Assert.Equal(
            "http://192.168.1.19:9119",
            HermesPairing.DeriveDashboardUrl("http://192.168.1.19:8642"));
        Assert.Equal(
            HermesPairing.LocalDefaultDashboardUrl,
            HermesPairing.DeriveDashboardUrl("http://127.0.0.1:8642/"));
    }

    [Fact]
    public void BuildCoreEnvSnippet_IncludesUrlAndKey()
    {
        var snip = HermesPairing.BuildCoreEnvSnippet("http://192.168.1.50:8741", "abc");
        Assert.Contains("ORBIT_CORE_URL=http://192.168.1.50:8741", snip, StringComparison.Ordinal);
        Assert.Contains("ORBIT_API_KEY=abc", snip, StringComparison.Ordinal);
    }
}
