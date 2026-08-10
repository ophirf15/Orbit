using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Orbit.Infrastructure.Updates;

namespace Orbit.Tests.Updates;

public sealed class GitHubReleasesUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_DetectsNewerRelease_WithoutLiveNetwork()
    {
        const string json =
            """
            {
              "tag_name": "v0.2.0",
              "body": "Ship it",
              "html_url": "https://github.com/ophirf15/Orbit/releases/tag/v0.2.0",
              "prerelease": false,
              "draft": false,
              "assets": [
                {
                  "name": "Orbit.appinstaller",
                  "browser_download_url": "https://example.com/Orbit.appinstaller"
                },
                {
                  "name": "Orbit-Setup-0.2.0.exe",
                  "browser_download_url": "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe"
                }
              ]
            }
            """;

        var handler = new ScriptedHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.Contains("/repos/ophirf15/Orbit/releases/latest", req.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            return JsonResponse(HttpStatusCode.OK, json);
        });

        using var checker = new GitHubReleasesUpdateChecker(handler, "ophirf15", "Orbit", new Uri("https://api.github.com"));
        var result = await checker.CheckAsync("0.1.0-phase17");

        Assert.True(result.Succeeded);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("v0.2.0", result.RemoteVersion);
        Assert.Equal("Ship it", result.ReleaseNotes);
        Assert.Equal("https://example.com/Orbit.appinstaller", result.AppInstallerUrl);
        Assert.Equal(
            "https://github.com/ophirf15/Orbit/releases/download/v0.2.0/Orbit-Setup-0.2.0.exe",
            result.SetupInstallerUrl);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_NotAvailable()
    {
        const string json =
            """
            {
              "tag_name": "v0.1.0",
              "html_url": "https://github.com/ophirf15/Orbit/releases/tag/v0.1.0",
              "prerelease": false,
              "draft": false,
              "assets": []
            }
            """;

        var handler = new ScriptedHandler(_ => JsonResponse(HttpStatusCode.OK, json));
        using var checker = new GitHubReleasesUpdateChecker(handler);
        var result = await checker.CheckAsync("0.1.0");
        Assert.True(result.Succeeded);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_HttpError_SurfacesMessage()
    {
        var handler = new ScriptedHandler(_ => JsonResponse(HttpStatusCode.NotFound, """{"message":"Not Found"}"""));
        using var checker = new GitHubReleasesUpdateChecker(handler);
        var result = await checker.CheckAsync("0.1.0");
        Assert.False(result.Succeeded);
        Assert.Contains("404", result.Error, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
