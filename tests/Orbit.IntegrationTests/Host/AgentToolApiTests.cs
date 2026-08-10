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

public sealed class AgentToolApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectId;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitAgentToolIT", Guid.NewGuid().ToString("N"));
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
        var ids = new DemoGraphSeed(factory).SeedIfEmpty();
        new SearchIndexRebuilder(factory).Rebuild();
        _projectId = ids.HarborProjectId;

        await _app.StartAsync();
        // Related-context bundles can exceed 5s under CI load when many hosts start in parallel.
        _client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
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
    public async Task OrbitGetProject_ReturnsProjectJson()
    {
        var response = await _client!.GetAsync($"v1/agent/tools/orbit_get_project?id={_projectId}");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_get_project", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal(_projectId, doc.RootElement.GetProperty("project").GetProperty("id").GetString());
    }

    [Fact]
    public async Task OrbitUpdateProject_SetsNamedAccentColor()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_update_project",
            new { id = _projectId, accentColor = "blue" });
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_update_project", doc.RootElement.GetProperty("tool").GetString());
        Assert.True(doc.RootElement.GetProperty("accentUpdated").GetBoolean());
        Assert.Equal("#0F6CBD", doc.RootElement.GetProperty("accentColor").GetString());
    }

    [Fact]
    public async Task OrbitGetWorkbench_ReturnsCells()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_get_workbench",
            new { });
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_get_workbench", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("cells").ValueKind);
        Assert.True(doc.RootElement.GetProperty("cells").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task OrbitGetProject_MissingId_IsBadRequest()
    {
        var response = await _client!.GetAsync("v1/agent/tools/orbit_get_project");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OrbitSearchFiles_AcceptsPostBody()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_search_files",
            new { q = "does-not-matter" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("orbit_search_files", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OrbitGetRelatedContext_ReturnsScopedBundle()
    {
        var response = await _client!.GetAsync(
            $"v1/agent/tools/orbit_get_related_context?targetType=project&targetId={_projectId}");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_get_related_context", doc.RootElement.GetProperty("tool").GetString());
        var bundle = doc.RootElement.GetProperty("bundle");
        Assert.Equal(_projectId, bundle.GetProperty("projectId").GetString());
        Assert.True(bundle.TryGetProperty("emails", out _));
        Assert.True(bundle.TryGetProperty("relatedEntities", out _));
        Assert.True(bundle.TryGetProperty("meetings", out var meetings));
        Assert.Equal(JsonValueKind.Array, meetings.ValueKind);
        Assert.True(meetings.GetArrayLength() >= 1);
        Assert.Contains(
            meetings.EnumerateArray(),
            m => (m.GetProperty("title").GetString() ?? string.Empty)
                .Contains("MetroFiber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ContextBundle_HttpEndpoint_ScopesExtractions()
    {
        var response = await _client!.GetAsync(
            $"v1/context/bundle?targetType=project&targetId={_projectId}");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(_projectId, doc.RootElement.GetProperty("projectId").GetString());
        foreach (var email in doc.RootElement.GetProperty("emails").EnumerateArray())
        {
            foreach (var extraction in email.GetProperty("extractions").EnumerateArray())
            {
                Assert.Equal(_projectId, extraction.GetProperty("projectId").GetString());
                var summary = extraction.GetProperty("summary").GetString() ?? string.Empty;
                Assert.DoesNotContain("Riverview", summary, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task TaskDependencyTools_Link_Read_Unlink_RoundTrip()
    {
        var predecessor = await CreateTaskAsync("Confirm phone line count with vendor");
        var successor = await CreateTaskAsync("Open phone lines with carrier");

        var link = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_link_tasks",
            new
            {
                predecessorTaskId = predecessor,
                successorTaskId = successor,
                dependencyType = "informs",
                expects = "line count",
                actor = "agent",
            });
        Assert.Equal(HttpStatusCode.Created, link.StatusCode);

        string dependencyId;
        await using (var linkStream = await link.Content.ReadAsStreamAsync())
        {
            using var linkDoc = await JsonDocument.ParseAsync(linkStream);
            dependencyId = linkDoc.RootElement.GetProperty("dependency").GetProperty("id").GetString()!;
        }

        var read = await _client.GetAsync($"v1/agent/tools/orbit_get_task_dependencies?taskId={successor}");
        read.EnsureSuccessStatusCode();
        await using (var readStream = await read.Content.ReadAsStreamAsync())
        {
            using var readDoc = await JsonDocument.ParseAsync(readStream);
            var waitingOn = readDoc.RootElement.GetProperty("waitingOn");
            Assert.Equal(1, waitingOn.GetArrayLength());
            var edge = waitingOn[0];
            Assert.Equal(predecessor, edge.GetProperty("taskId").GetString());
            Assert.Equal("line count", edge.GetProperty("expects").GetString());
            Assert.False(edge.GetProperty("satisfied").GetBoolean());
            Assert.Equal(0, readDoc.RootElement.GetProperty("feeds").GetArrayLength());
        }

        var unlink = await _client.PostAsJsonAsync(
            "v1/agent/tools/orbit_unlink_tasks",
            new { dependencyId, actor = "user" });
        unlink.EnsureSuccessStatusCode();

        var afterUnlink = await _client.PostAsJsonAsync(
            "v1/agent/tools/orbit_unlink_tasks",
            new { dependencyId, actor = "user" });
        Assert.Equal(HttpStatusCode.NotFound, afterUnlink.StatusCode);
    }

    [Fact]
    public async Task LinkTasks_RejectsCircularDependency()
    {
        var a = await CreateTaskAsync("Cycle guard task A");
        var b = await CreateTaskAsync("Cycle guard task B");

        var forward = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_link_tasks",
            new { predecessorTaskId = a, successorTaskId = b, dependencyType = "blocks" });
        Assert.Equal(HttpStatusCode.Created, forward.StatusCode);

        var reverse = await _client.PostAsJsonAsync(
            "v1/agent/tools/orbit_link_tasks",
            new { predecessorTaskId = b, successorTaskId = a, dependencyType = "blocks" });
        Assert.Equal(HttpStatusCode.BadRequest, reverse.StatusCode);

        var selfLink = await _client.PostAsJsonAsync(
            "v1/agent/tools/orbit_link_tasks",
            new { predecessorTaskId = a, successorTaskId = a, dependencyType = "blocks" });
        Assert.Equal(HttpStatusCode.BadRequest, selfLink.StatusCode);
    }

    [Fact]
    public async Task SuggestTaskLinks_ProposesContingentPair()
    {
        await CreateTaskAsync("Confirm sprinkler riser count with inspector");
        var consumer = await CreateTaskAsync("Order sprinkler riser parts");

        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_suggest_task_links",
            new { id = consumer });
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var suggestions = doc.RootElement.GetProperty("suggestions");
        Assert.True(suggestions.GetArrayLength() >= 1);
        Assert.Contains(
            suggestions.EnumerateArray(),
            s => s.GetProperty("suggestionType").GetString() == "link_tasks");
    }

    private async Task<string> CreateTaskAsync(string title)
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_create_task",
            new { title, projectId = _projectId, actor = "agent" });
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.GetProperty("task").GetProperty("id").GetString()!;
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
