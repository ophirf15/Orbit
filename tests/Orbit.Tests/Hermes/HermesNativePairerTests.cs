using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesEnvFileTests
{
    [Fact]
    public void Upsert_AddsAndReplacesKeys_PreservesComments()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".env");
        try
        {
            File.WriteAllText(path, "# keep me\nFOO=1\nAPI_SERVER_KEY=old\n");
            var changed = HermesEnvFile.Upsert(
                path,
                new Dictionary<string, string>
                {
                    ["API_SERVER_KEY"] = "new",
                    ["ORBIT_CORE_URL"] = "http://127.0.0.1:8741",
                    ["API_SERVER_ENABLED"] = "true",
                });

            Assert.True(changed);
            var text = File.ReadAllText(path);
            Assert.Contains("# keep me", text, StringComparison.Ordinal);
            Assert.Contains("FOO=1", text, StringComparison.Ordinal);
            Assert.Contains("API_SERVER_KEY=new", text, StringComparison.Ordinal);
            Assert.DoesNotContain("API_SERVER_KEY=old", text, StringComparison.Ordinal);
            Assert.Contains("ORBIT_CORE_URL=http://127.0.0.1:8741", text, StringComparison.Ordinal);
            Assert.Contains("API_SERVER_ENABLED=true", text, StringComparison.Ordinal);
            Assert.Equal("new", HermesEnvFile.Get(path, "API_SERVER_KEY"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Upsert_IsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".env");
        try
        {
            var values = new Dictionary<string, string> { ["API_SERVER_ENABLED"] = "true" };
            Assert.True(HermesEnvFile.Upsert(path, values));
            Assert.False(HermesEnvFile.Upsert(path, values));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class HermesNativePairerTests
{
    [Fact]
    public void Detect_FindsConfiguredHome()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "TERMINAL_ENV=local\n");
            var info = HermesNativePairer.Detect(dir);
            Assert.True(info.LooksInstalled);
            Assert.Equal(dir, info.HermesHome);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pair_WritesApiAndOrbitEnv_WithoutGateway()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit-hermes-pair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ".env"), "# seed\nTERMINAL_ENV=local\n");
            var result = HermesNativePairer.Pair(
                hermesHomeOverride: dir,
                orbitCoreUrl: "http://127.0.0.1:8741",
                orbitApiKey: "core-secret",
                preferredApiServerKey: "api-secret-fixed",
                restartGateway: false);

            Assert.Equal(dir, result.HermesHome);
            Assert.Equal("http://127.0.0.1:8642", result.ApiBaseUrl);
            Assert.Equal("api-secret-fixed", result.ApiServerKey);
            Assert.True(result.EnvChanged);
            Assert.True(result.ApiServerEnabledWritten);
            Assert.True(result.OrbitEnvWritten);
            Assert.False(result.GatewayRestartAttempted);

            var env = HermesEnvFile.Read(Path.Combine(dir, ".env"));
            Assert.Equal("true", env["API_SERVER_ENABLED"]);
            Assert.Equal("api-secret-fixed", env["API_SERVER_KEY"]);
            Assert.Equal("http://127.0.0.1:8741", env["ORBIT_CORE_URL"]);
            Assert.Equal("core-secret", env["ORBIT_API_KEY"]);
            Assert.True(File.Exists(Path.Combine(dir, "SOUL.md")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
