using Orbit.Infrastructure.Updates;

namespace Orbit.Tests.Updates;

public sealed class OrbitSetupUpdateApplierTests
{
    [Theory]
    [InlineData("https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/foo", true)]
    [InlineData("https://release-assets.githubusercontent.com/foo", true)]
    [InlineData("http://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe", false)]
    [InlineData("https://evil.example/Orbit-Setup-0.2.0.exe", false)]
    [InlineData("not-a-url", false)]
    public void IsTrustedReleaseAssetUrl_AllowlistsGitHubHosts(string url, bool expected)
    {
        Assert.Equal(expected, OrbitSetupUpdateApplier.IsTrustedReleaseAssetUrl(url));
    }
}
