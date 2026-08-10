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

public sealed class TelegramContinuityApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectId;
    private SqliteConnectionFactory? _factory;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitTelegramIT", Guid.NewGuid().ToString("N"));
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

        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        var ids = new DemoGraphSeed(_factory).SeedIfEmpty();
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
    public async Task ConversationSync_TelegramChannel_UpsertsByHermesSession()
    {
        var create = await _client!.PostAsJsonAsync(
            "v1/conversations/sync",
            new
            {
                channel = "telegram",
                hermesSessionId = "tg-sess-1",
                hermesSessionKey = "tg-key-1",
                title = "Away chat",
            });
        create.EnsureSuccessStatusCode();
        await using var createStream = await create.Content.ReadAsStreamAsync();
        using var createDoc = await JsonDocument.ParseAsync(createStream);
        var firstId = createDoc.RootElement.GetProperty("conversation").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(firstId));
        Assert.Equal("telegram", createDoc.RootElement.GetProperty("conversation").GetProperty("channel").GetString());

        var update = await _client!.PostAsJsonAsync(
            "v1/conversations/sync",
            new
            {
                channel = "telegram",
                hermesSessionId = "tg-sess-1",
                title = "Away chat (updated)",
            });
        update.EnsureSuccessStatusCode();
        await using var updateStream = await update.Content.ReadAsStreamAsync();
        using var updateDoc = await JsonDocument.ParseAsync(updateStream);
        Assert.Equal(firstId, updateDoc.RootElement.GetProperty("conversation").GetProperty("id").GetString());
        Assert.Equal(
            "Away chat (updated)",
            updateDoc.RootElement.GetProperty("conversation").GetProperty("title").GetString());
    }

    [Fact]
    public async Task CreateTask_WithTelegramProvenance_AppearsInRemoteActivity()
    {
        var sync = await _client!.PostAsJsonAsync(
            "v1/conversations/sync",
            new
            {
                channel = "telegram",
                hermesSessionId = "tg-sess-audit",
                title = "Provenance chat",
            });
        sync.EnsureSuccessStatusCode();

        var create = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_create_task",
            new
            {
                title = "Follow up with Maria",
                projectId = _projectId,
                actor = "agent",
                provenance = new
                {
                    actor = "hermes",
                    channel = "telegram",
                    hermesSessionId = "tg-sess-audit",
                    externalUserId = "tg-user-42",
                },
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var activity = await _client!.GetAsync("v1/activity/remote");
        activity.EnsureSuccessStatusCode();
        await using var stream = await activity.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);

        var conversations = doc.RootElement.GetProperty("conversations");
        Assert.Contains(
            conversations.EnumerateArray(),
            c => c.GetProperty("hermesSessionId").GetString() == "tg-sess-audit");

        var audits = doc.RootElement.GetProperty("auditEvents");
        Assert.Contains(
            audits.EnumerateArray(),
            a =>
            {
                if (a.GetProperty("eventType").GetString() != "task.created")
                {
                    return false;
                }

                var channel = a.TryGetProperty("channel", out var ch) ? ch.GetString() : null;
                var session = a.TryGetProperty("hermesSessionId", out var hs) ? hs.GetString() : null;
                var detail = a.TryGetProperty("detailJson", out var dj) ? dj.GetString() : null;
                return channel == "telegram"
                    && session == "tg-sess-audit"
                    && detail is not null
                    && detail.Contains("Follow up with Maria", StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task SimulatedTelegramReadProject_WorksViaAgentTool()
    {
        var response = await _client!.GetAsync($"v1/agent/tools/orbit_get_project?id={_projectId}");
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_get_project", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal(_projectId, doc.RootElement.GetProperty("project").GetProperty("id").GetString());
    }

    [Fact]
    public void Solution_HasNoTelegramBotPackage()
    {
        var repoRoot = FindRepoRoot();
        var csprojs = Directory.GetFiles(repoRoot, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(csprojs);
        foreach (var path in csprojs)
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain("Telegram.Bot", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TelegramBot", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Orbit.sln"))
                || File.Exists(Path.Combine(dir.FullName, "build.ps1")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repo root from test base directory.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
