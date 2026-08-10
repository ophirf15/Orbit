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

public sealed class FileCapabilityTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectId;
    private string? _folderFiles;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitFileCapIT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        _folderFiles = Path.Combine(_tempRoot, "project-docs");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(_folderFiles);
        File.WriteAllText(Path.Combine(_folderFiles, "W-9-Vendor.txt"), "Form W-9 taxpayer id Acme Cable");
        File.WriteAllText(Path.Combine(_folderFiles, "proposal.txt"), "Proposal for Harbor Court property internet");

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
        _projectId = Guid.NewGuid().ToString("D");
        var now = DateTime.UtcNow.ToString("O");
        using (var connection = factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO projects (id, name, status, created_at, updated_at) VALUES ($id, 'Harbor Court', 'active', $t, $t);";
            cmd.Parameters.AddWithValue("$id", _projectId);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_options.BaseUrl + "/"), Timeout = TimeSpan.FromSeconds(10) };
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
    public async Task AttachIndexSearchAndLinkW9()
    {
        var attach = await _client!.PostAsJsonAsync(
            $"v1/projects/{_projectId}/folders",
            new { path = _folderFiles });
        Assert.Equal(HttpStatusCode.Created, attach.StatusCode);

        using var search = await _client.GetAsync($"v1/files/search?q=W-9&projectId={_projectId}");
        search.EnsureSuccessStatusCode();
        await using var stream = await search.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var results = doc.RootElement.GetProperty("results");
        Assert.True(results.GetArrayLength() >= 1);
        var fileId = results[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fileId));

        var orgId = Guid.NewGuid().ToString("D");
        var link = await _client.PostAsJsonAsync(
            $"v1/files/{fileId}/links",
            new { projectId = _projectId, entityType = "organization", entityId = orgId });
        link.EnsureSuccessStatusCode();
        var linkBody = await link.Content.ReadAsStringAsync();
        Assert.Contains("organization", linkBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("project", linkBody, StringComparison.OrdinalIgnoreCase);

        using var openMeta = await _client.GetAsync($"v1/files/{fileId}");
        openMeta.EnsureSuccessStatusCode();
        var meta = await openMeta.Content.ReadAsStringAsync();
        Assert.Contains("W-9", meta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalMutationRoutes_AreForbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _client!.PostAsync("v1/files/external/delete", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("v1/files/external/rename", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("v1/files/external/move", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsync("v1/files/external/write", null)).StatusCode);
    }

    [Fact]
    public async Task FilesRead_DeniesPathOutsideAttachedRoots()
    {
        var outside = Path.Combine(_tempRoot!, "outside.txt");
        File.WriteAllText(outside, "secret");
        var response = await _client!.PostAsJsonAsync("v1/files/read", new { path = outside });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("outside attached", body, StringComparison.OrdinalIgnoreCase);
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
