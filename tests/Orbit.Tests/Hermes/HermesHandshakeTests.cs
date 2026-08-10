using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesHandshakeTests
{
    [Fact]
    public async Task ProbeAsync_ReportsNotConnectedForBadUrl()
    {
        var probe = await HermesHandshake.ProbeAsync("not-a-url", null);
        Assert.Equal(HermesHandshakeState.NotConnected, probe.State);
        Assert.False(probe.Connected);
    }

    [Fact]
    public async Task ConnectThisPcAsync_NotInstalled_WhenHomeMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "orbit-no-hermes-" + Guid.NewGuid().ToString("N"));
        var result = await HermesHandshake.ConnectThisPcAsync(
            "http://127.0.0.1:8741",
            "core-key",
            hermesHomeOverride: missing);

        Assert.Equal(HermesHandshakeState.NotInstalled, result.State);
        Assert.False(result.Connected);
    }

    [Fact]
    public async Task ConnectThisPcAsync_ExchangesKeysAndProvisions_EvenIfApiDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-handshake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "TERMINAL_ENV=local\n");
            File.WriteAllText(Path.Combine(dir, "config.yaml"), "model:\n  default: test\n");

            // Use a dead port so WaitForApi times out quickly-ish — override port via env first.
            HermesEnvFile.Upsert(
                Path.Combine(dir, ".env"),
                new Dictionary<string, string>
                {
                    ["API_SERVER_PORT"] = "1",
                    ["API_SERVER_HOST"] = "127.0.0.1",
                });

            var result = await HermesHandshake.ConnectThisPcAsync(
                "http://127.0.0.1:8741",
                "core-secret",
                preferredHermesApiKey: "hermes-key-fixed",
                hermesHomeOverride: dir,
                verifyApi: false);

            Assert.True(result.Connected);
            Assert.Equal(HermesHandshakeState.Connected, result.State);
            Assert.Equal("hermes-key-fixed", result.ApiServerKey);
            Assert.Contains("API_SERVER_ENABLED=true", File.ReadAllText(Path.Combine(dir, ".env")), StringComparison.Ordinal);
            Assert.Contains("ORBIT_API_KEY=core-secret", File.ReadAllText(Path.Combine(dir, ".env")), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dir, "SOUL.md")));
            Assert.True(Directory.Exists(Path.Combine(dir, "plugins", "orbit")));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
