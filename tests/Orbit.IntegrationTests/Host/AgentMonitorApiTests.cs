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

/// <summary>
/// Hermes monitor fuel (ADR 0028 / plan 021 U3): /v1/changes, /v1/pulse/delta,
/// /v1/tasks/blocked, /v1/agent/snapshot.
/// </summary>
public sealed class AgentMonitorApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectId;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitAgentMonitorIT", Guid.NewGuid().ToString("N"));
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
    public async Task Changes_EmptyDelta_WhenNothingHasHappened()
    {
        var response = await _client!.GetAsync("v1/changes?cursor=0");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(0, doc.RootElement.GetProperty("cursor").GetInt64());
        Assert.Equal(0, doc.RootElement.GetProperty("nextCursor").GetInt64());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("events").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("events").EnumerateArray());
    }

    [Fact]
    public async Task PulseDelta_EmptyChanged_WhenNothingHasHappened()
    {
        var response = await _client!.GetAsync("v1/pulse/delta?cursor=0");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(0, doc.RootElement.GetProperty("nextCursor").GetInt64());
        Assert.Empty(doc.RootElement.GetProperty("changed").EnumerateArray());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("concerns").ValueKind);
    }

    [Fact]
    public async Task Changes_CursorIsMonotonic_AcrossTaskUpdate()
    {
        var taskId = await CreateTaskAsync();

        // task.created is not a tracked change-log source event — baseline must still be empty.
        var before = await GetChangesAsync(0);
        Assert.Empty(before.RootElement.GetProperty("events").EnumerateArray());

        var update = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_update_task",
            new { id = taskId, nextAction = "Follow up" });
        update.EnsureSuccessStatusCode();

        var page = await WaitForChangeAsync(taskId, TimeSpan.FromSeconds(20));
        var events = page.RootElement.GetProperty("events").EnumerateArray().ToList();
        Assert.Single(events);
        var revision = events[0].GetProperty("revision").GetInt64();
        Assert.True(revision > 0);
        Assert.Equal("task", events[0].GetProperty("entityType").GetString());
        Assert.Equal(taskId, events[0].GetProperty("entityId").GetString());
        Assert.False(events[0].GetProperty("tombstone").GetBoolean());
        var nextCursor = page.RootElement.GetProperty("nextCursor").GetInt64();
        Assert.Equal(revision, nextCursor);

        // Re-polling with the returned cursor yields an empty, stable page (monotonic + idempotent).
        var again = await GetChangesAsync(nextCursor);
        Assert.Empty(again.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(nextCursor, again.RootElement.GetProperty("nextCursor").GetInt64());
    }

    [Fact]
    public async Task AgentSnapshot_IsStableAcrossReadsWithNoMutations()
    {
        var first = await _client!.GetStringAsync("v1/agent/snapshot");
        var second = await _client!.GetStringAsync("v1/agent/snapshot");

        Assert.Equal(Normalize(first), Normalize(second));
    }

    [Fact]
    public async Task AgentSnapshot_HasStableSchemaAndNoVolatileTimestamps()
    {
        var response = await _client!.GetAsync("v1/agent/snapshot");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        Assert.Equal("orbit.agent.snapshot.v1", root.GetProperty("schema").GetString());
        Assert.True(root.TryGetProperty("changeCursor", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("projects").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tasks").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("meetings").ValueKind);

        var raw = root.GetRawText();
        Assert.DoesNotContain("startsAt", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksBlocked_ReturnsOnlyBlockedTasks()
    {
        var response = await _client!.GetAsync("v1/tasks/blocked");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("tasks").ValueKind);
        foreach (var task in doc.RootElement.GetProperty("tasks").EnumerateArray())
        {
            Assert.Equal(TaskStatuses.Blocked, task.GetProperty("status").GetString());
        }
    }

    private static string Normalize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("requestId"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<string> CreateTaskAsync()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_create_task",
            new { title = "Change-log probe task", projectId = _projectId });
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.GetProperty("task").GetProperty("id").GetString()!;
    }

    private async Task<JsonDocument> GetChangesAsync(long cursor)
    {
        var response = await _client!.GetAsync($"v1/changes?cursor={cursor}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private async Task<JsonDocument> WaitForChangeAsync(string entityId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var page = await GetChangesAsync(0);
            if (page.RootElement.GetProperty("events").EnumerateArray()
                .Any(e => e.GetProperty("entityId").GetString() == entityId))
            {
                return page;
            }

            page.Dispose();
            await Task.Delay(100);
        }

        throw new TimeoutException($"Change-log event for entity '{entityId}' did not appear within {timeout}.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
