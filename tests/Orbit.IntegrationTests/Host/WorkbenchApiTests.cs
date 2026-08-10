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

public sealed class WorkbenchApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitWorkbenchIT", Guid.NewGuid().ToString("N"));
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

        // Seed after migrator ran inside BuildApp.
        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        new DemoGraphSeed(factory).SeedIfEmpty();
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
    public async Task Workbench_IncludesSeededProjectsAndLimbo()
    {
        using var response = await _client!.GetAsync("v1/workbench");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var cells = doc.RootElement.GetProperty("cells");
        Assert.True(cells.GetArrayLength() >= 2);
        var limbo = doc.RootElement.GetProperty("limbo");
        Assert.True(limbo.GetArrayLength() >= 1);
        Assert.Contains(
            limbo.EnumerateArray(),
            n => n.GetProperty("originalText").GetString() == "Call him back about proposal");
    }

    [Fact]
    public async Task PostLimboNote_AppearsAfterReopenStore()
    {
        var response = await _client!.PostAsJsonAsync("v1/notes", new { text = "Buy paint samples", projectId = (string?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var createdStream = await response.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var noteId = createdDoc.RootElement.GetProperty("noteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(noteId));
        var persistedNoteId = noteId!;

        using var workbench = await _client.GetAsync("v1/workbench");
        workbench.EnsureSuccessStatusCode();
        var json = await workbench.Content.ReadAsStringAsync();
        Assert.Contains("Buy paint samples", json, StringComparison.Ordinal);

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(_options!.LocalDataRoot));
        var snapshot = new WorkbenchReadStore(factory).GetSnapshot();
        Assert.Contains(snapshot.Limbo, n => n.Id == persistedNoteId && n.OriginalText == "Buy paint samples");
    }

    [Fact]
    public async Task PostProjectNote_AddsCellLine()
    {
        using var projectsResponse = await _client!.GetAsync("v1/projects");
        projectsResponse.EnsureSuccessStatusCode();
        await using var stream = await projectsResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var projectId = doc.RootElement.GetProperty("projects")[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(projectId));

        var create = await _client.PostAsJsonAsync("v1/notes", new { text = "Site walk Thursday", projectId });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        using var workbench = await _client.GetAsync("v1/workbench");
        workbench.EnsureSuccessStatusCode();
        var body = await workbench.Content.ReadAsStringAsync();
        Assert.Contains("Site walk Thursday", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostEmptyNote_ReturnsBadRequest()
    {
        var response = await _client!.PostAsJsonAsync("v1/notes", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProjectContext_ReturnsDetail()
    {
        using var projectsResponse = await _client!.GetAsync("v1/projects");
        projectsResponse.EnsureSuccessStatusCode();
        await using var stream = await projectsResponse.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var projectId = doc.RootElement.GetProperty("projects")[0].GetProperty("id").GetString();

        using var context = await _client.GetAsync($"v1/projects/{projectId}/context");
        context.EnsureSuccessStatusCode();
        var json = await context.Content.ReadAsStringAsync();
        Assert.Contains("tasks", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notes", json, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
