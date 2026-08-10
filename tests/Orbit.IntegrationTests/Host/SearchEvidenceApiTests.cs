using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;

namespace Orbit.IntegrationTests.HostApi;

public sealed class SearchEvidenceApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private DemoGraphIds? _ids;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitSearchEvidenceIT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);

        var port = GetFreePort();
        _options = new HostOptions
        {
            BindAddress = "127.0.0.1",
            Port = port,
            BaseUrl = $"http://127.0.0.1:{port}",
            ApiKey = null,
            LocalDataRoot = data,
            GeneratedFilesRoot = generated,
        };

        HostStartupGuard.EnsureMayListen(_options);
        var builder = OrbitHostWebApp.CreateBuilder(_options);
        _app = OrbitHostWebApp.BuildApp(builder, _options);

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        _ids = new DemoGraphSeed(factory).SeedIfEmpty();
        new SearchIndexRebuilder(factory).Rebuild();

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    [Fact]
    public async Task GetSearch_ReturnsRankedHits()
    {
        var response = await _client!.GetAsync("v1/search?q=Harbor Court");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("results").ValueKind);
        Assert.True(doc.RootElement.GetProperty("results").GetArrayLength() > 0);
    }

    [Fact]
    public async Task GetEvidence_Ein_ReturnsCitations()
    {
        var response = await _client!.GetAsync("v1/evidence/query?q=" + Uri.EscapeDataString("What's our EIN?"));
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("ein", doc.RootElement.GetProperty("answerType").GetString());
        Assert.Equal(_ids!.AcmeEin, doc.RootElement.GetProperty("value").GetString());
        Assert.True(doc.RootElement.GetProperty("citations").GetArrayLength() > 0);
    }

    [Fact]
    public async Task PostEvidence_HarborCourtStatus_ExcludesRiverview()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/evidence/query",
            new { question = "status on Harbor Court", projectId = _ids!.HarborProjectId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("project_status", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Riverview modem", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Harbor Court", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentTools_SearchAndEvidence_Work()
    {
        var search = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_search",
            new { q = "Alex" });
        search.EnsureSuccessStatusCode();
        var searchBody = await search.Content.ReadAsStringAsync();
        Assert.Contains("orbit_search", searchBody, StringComparison.OrdinalIgnoreCase);

        var evidence = await _client.GetAsync(
            "v1/agent/tools/orbit_answer_with_evidence?q=" + Uri.EscapeDataString("EIN"));
        evidence.EnsureSuccessStatusCode();
        var evidenceBody = await evidence.Content.ReadAsStringAsync();
        Assert.Contains("orbit_answer_with_evidence", evidenceBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(_ids!.AcmeEin, evidenceBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capabilities_SearchAvailable()
    {
        var response = await _client!.GetAsync("v1/capabilities");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":\"search\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\":\"search\",\"route\":\"/v1/search\",\"status\":\"stub\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orbit_search", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orbit_answer_with_evidence", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Search_MissingQuery_IsBadRequest()
    {
        var response = await _client!.GetAsync("v1/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
}
