using Orbit.Core.Updates;

namespace Orbit.Tests.Updates;

public sealed class GitHubReleaseParserTests
{
    [Fact]
    public void Parse_ReadsTagNotesAssets()
    {
        const string json =
            """
            {
              "tag_name": "v0.2.0",
              "name": "Orbit 0.2.0",
              "body": "Notes here",
              "html_url": "https://github.com/ophirf15/Orbit/releases/tag/v0.2.0",
              "prerelease": false,
              "draft": false,
              "assets": [
                {
                  "name": "Orbit.appinstaller",
                  "browser_download_url": "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit.appinstaller"
                },
                {
                  "name": "Orbit_x64.msix",
                  "browser_download_url": "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit_x64.msix"
                },
                {
                  "name": "Orbit-Setup-0.2.0.exe",
                  "browser_download_url": "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe"
                }
              ]
            }
            """;

        var release = GitHubReleaseParser.Parse(json);
        Assert.Equal("v0.2.0", release.TagName);
        Assert.Equal("Notes here", release.Body);
        Assert.Equal(
            "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit.appinstaller",
            GitHubReleaseParser.FindAssetUrl(release, ".appinstaller"));
        Assert.Equal(
            "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit_x64.msix",
            GitHubReleaseParser.FindAssetUrl(release, ".msix", ".msixbundle"));
        Assert.Equal(
            "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe",
            GitHubReleaseParser.FindSetupInstallerUrl(release));
    }
}
