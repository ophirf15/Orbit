using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;
using Orbit.Infrastructure.Data;

namespace Orbit.IntegrationTests.HostApi;

public sealed class EmailIngestionApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectA;
    private string? _projectB;
    private string? _msgPath;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitEmailIT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);

        _msgPath = Path.Combine(_tempRoot, "sample.msg");
        var fixture = ResolveFixture();
        File.Copy(fixture, _msgPath, overwrite: true);

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
        _projectA = Guid.NewGuid().ToString("D");
        _projectB = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using (var connection = factory.CreateConnection())
        {
            InsertProject(connection, _projectA, "Harbor Court", now);
            InsertProject(connection, _projectB, "Riverview", now);
        }

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
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
    public async Task IngestAndLinkSameEmailToTwoProjects()
    {
        var ingest = await _client!.PostAsJsonAsync(
            "v1/emails/ingest",
            new { path = _msgPath, projectIds = new[] { _projectA } });
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        await using var ingestStream = await ingest.Content.ReadAsStreamAsync();
        using var ingestDoc = await JsonDocument.ParseAsync(ingestStream);
        var emailId = ingestDoc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(emailId));
        Assert.Equal("Orbit fixture subject", ingestDoc.RootElement.GetProperty("subject").GetString());
        var rawPath = ingestDoc.RootElement.GetProperty("rawPath").GetString();
        Assert.True(File.Exists(rawPath!));
        Assert.Contains(Path.Combine("generated", "emails"), rawPath!, StringComparison.OrdinalIgnoreCase);

        var link = await _client!.PostAsJsonAsync(
            $"v1/emails/{emailId}/projects",
            new { projectId = _projectB });
        link.EnsureSuccessStatusCode();

        using var get = await _client.GetAsync($"v1/emails/{emailId}");
        get.EnsureSuccessStatusCode();
        await using var getStream = await get.Content.ReadAsStreamAsync();
        using var getDoc = await JsonDocument.ParseAsync(getStream);
        var projects = getDoc.RootElement.GetProperty("projectIds")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains(_projectA, projects);
        Assert.Contains(_projectB, projects);
        Assert.Equal(2, projects.Count);

        // Re-ingest same file must not clone the artifact.
        var again = await _client.PostAsJsonAsync(
            "v1/emails/ingest",
            new { path = _msgPath, projectIds = Array.Empty<string>() });
        again.EnsureSuccessStatusCode();
        await using var againStream = await again.Content.ReadAsStreamAsync();
        using var againDoc = await JsonDocument.ParseAsync(againStream);
        Assert.Equal(emailId, againDoc.RootElement.GetProperty("id").GetString());
        Assert.True(againDoc.RootElement.GetProperty("wasExisting").GetBoolean());
    }

    [Fact]
    public async Task IngestMultipartFile_MatchesOutlookAddInPath()
    {
        await using var fileStream = File.OpenRead(_msgPath!);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.ms-outlook");
        content.Add(fileContent, "file", "outlook-push.msg");
        content.Add(new StringContent(_projectA!), "projectIds");

        using var ingest = await _client!.PostAsync("v1/emails/ingest", content);
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);

        await using var ingestStream = await ingest.Content.ReadAsStreamAsync();
        using var ingestDoc = await JsonDocument.ParseAsync(ingestStream);
        Assert.Equal("Orbit fixture subject", ingestDoc.RootElement.GetProperty("subject").GetString());
        Assert.False(string.IsNullOrWhiteSpace(ingestDoc.RootElement.GetProperty("id").GetString()));
        Assert.False(ingestDoc.RootElement.GetProperty("wasExisting").GetBoolean());

        var emailId = ingestDoc.RootElement.GetProperty("id").GetString();
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

    private static void InsertProject(Microsoft.Data.Sqlite.SqliteConnection connection, string id, string name, string now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, $name, 'active', $t, $t);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
