using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;

namespace Orbit.IntegrationTests.HostApi;

public sealed class OutlookWebAddInApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectA;
    private string? _msgPath;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitOutlookWebIT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);

        _msgPath = Path.Combine(_tempRoot, "sample.msg");
        File.Copy(ResolveFixture(), _msgPath, overwrite: true);

        var port = GetFreePort();
        _options = new HostOptions
        {
            BindAddress = "127.0.0.1",
            Port = port,
            BaseUrl = $"http://127.0.0.1:{port}",
            ApiKey = "test-outlook-key",
            LocalDataRoot = data,
            GeneratedFilesRoot = generated,
        };

        HostStartupGuard.EnsureMayListen(_options);
        var builder = OrbitHostWebApp.CreateBuilder(_options);
        builder.Services.AddSingleton<IOutlookMsgExport>(new FixtureOutlookMsgExport(_msgPath));
        _app = OrbitHostWebApp.BuildApp(builder, _options);

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        _projectA = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using (var connection = factory.CreateConnection())
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, $name, 'active', $t, $t);";
            cmd.Parameters.AddWithValue("$id", _projectA);
            cmd.Parameters.AddWithValue("$name", "Harbor Court");
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-outlook-key");
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
    public async Task Bootstrap_IsAnonymousAndReturnsKeyOnLoopback()
    {
        using var anonymous = new HttpClient
        {
            BaseAddress = new Uri(_options!.BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        using var res = await anonymous.GetAsync("v1/outlook-addin/bootstrap");
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("test-outlook-key", doc.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(_options!.ResolveBaseUrl(), doc.RootElement.GetProperty("hostBaseUrl").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("emailsFromOutlook").GetString()));
    }

    [Fact]
    public async Task FromOutlook_RequiresMemo()
    {
        using var res = await _client!.PostAsJsonAsync(
            "v1/emails/from-outlook",
            new { internetMessageId = "<x@y>", projectIds = new[] { _projectA } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task FromOutlook_IngestsMsgAndCapturesMemo()
    {
        using var res = await _client!.PostAsJsonAsync(
            "v1/emails/from-outlook",
            new
            {
                internetMessageId = "<fixture@orbit.local>",
                subject = "Orbit fixture subject",
                memo = "Follow up with the GC on the change order.",
                projectIds = new[] { _projectA },
                preferSelection = true,
            });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        await using var stream = await res.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("Orbit fixture subject", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal("Follow up with the GC on the change order.", doc.RootElement.GetProperty("memo").GetString());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("noteId").GetString()));

        var emailId = doc.RootElement.GetProperty("id").GetString();
        using var get = await _client.GetAsync($"v1/emails/{emailId}");
        get.EnsureSuccessStatusCode();
        await using var getStream = await get.Content.ReadAsStreamAsync();
        using var getDoc = await JsonDocument.ParseAsync(getStream);
        var projects = getDoc.RootElement.GetProperty("projectIds")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(_projectA, projects);
    }

    private static string ResolveFixture()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "fixtures", "sample.msg"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "sample.msg")),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("Missing tests/fixtures/sample.msg");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FixtureOutlookMsgExport : IOutlookMsgExport
    {
        private readonly string _sourceMsg;

        public FixtureOutlookMsgExport(string sourceMsg) => _sourceMsg = sourceMsg;

        public Task<OutlookMsgExportResult> ExportAsync(OutlookMsgExportRequest request, CancellationToken ct = default)
        {
            var dir = Path.Combine(Path.GetTempPath(), "OrbitOutlookPush");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".msg");
            File.Copy(_sourceMsg, path, overwrite: true);
            return Task.FromResult(new OutlookMsgExportResult
            {
                Ok = true,
                MsgPath = path,
                Subject = request.Subject ?? "Orbit fixture subject",
            });
        }
    }
}
