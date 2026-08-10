using Orbit.Core.Settings;
using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesLocalBundleWriterTests
{
    [Fact]
    public void Write_CreatesComposeAndEnv()
    {
        var root = Path.Combine(Path.GetTempPath(), "orbit-hermes-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = HermesLocalBundleWriter.Write(root, "http://host.docker.internal:8741", "core-key");
            Assert.True(Directory.Exists(bundle.Directory));
            Assert.Equal(64, bundle.ApiServerKey.Length);
            var env = File.ReadAllText(Path.Combine(bundle.Directory, ".env"));
            Assert.Contains("API_SERVER_KEY=" + bundle.ApiServerKey, env, StringComparison.Ordinal);
            Assert.Contains("ORBIT_API_KEY=core-key", env, StringComparison.Ordinal);
            Assert.Contains("HERMES_DASHBOARD_BASIC_AUTH_USERNAME=orbit", env, StringComparison.Ordinal);
            Assert.Contains("HERMES_DASHBOARD_BASIC_AUTH_PASSWORD=" + bundle.DashboardPassword, env, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(bundle.Directory, "docker-compose.yml")));
            Assert.Contains("9119:9119", File.ReadAllText(Path.Combine(bundle.Directory, "docker-compose.yml")));
            Assert.Contains("gateway", File.ReadAllText(Path.Combine(bundle.Directory, "docker-compose.yml")));
            Assert.True(File.Exists(Path.Combine(bundle.Directory, "dashboard-login.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
