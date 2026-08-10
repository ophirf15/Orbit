using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;

namespace Orbit.IntegrationTests.HostApi;

public sealed class MalleabilityApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private string? _repoRoot;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitMalleabilityIT", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        _repoRoot = Path.Combine(_tempRoot, "repo");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(_repoRoot);

        var port = GetFreePort();
        _options = new HostOptions
        {
            BindAddress = "127.0.0.1",
            Port = port,
            BaseUrl = $"http://127.0.0.1:{port}",
            ApiKey = null,
            LocalDataRoot = data,
            GeneratedFilesRoot = generated,
            DeveloperMode = true,
            SourceRepoRoot = _repoRoot,
            DeveloperRemoteOverride = false,
        };

        HostStartupGuard.EnsureMayListen(_options);
        var builder = OrbitHostWebApp.CreateBuilder(_options);
        _app = OrbitHostWebApp.BuildApp(builder, _options);
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
    public async Task AddCustomField_AndList()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_add_custom_field",
            new
            {
                entityType = "workstream",
                key = "utility_account_number",
                fieldType = "text",
                validation = new { maxLength = 64 },
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        Assert.Equal("orbit_add_custom_field", doc.RootElement.GetProperty("tool").GetString());

        var list = await _client.GetAsync("v1/custom-fields?entityType=workstream");
        list.EnsureSuccessStatusCode();
        var listBody = await list.Content.ReadAsStringAsync();
        Assert.Contains("utility_account_number", listBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Layout_Save_And_Revert()
    {
        var client = _client!;
        var save = await client.PostAsJsonAsync(
            "v1/agent/tools/orbit_save_layout",
            new { name = "Lanes", schemaJson = """{"lanes":[{"id":"one"}]}""" });
        Assert.Equal(HttpStatusCode.Created, save.StatusCode);
        await using var saveStream = await save.Content.ReadAsStreamAsync();
        using var saveDoc = await JsonDocument.ParseAsync(saveStream);
        var layoutId = saveDoc.RootElement.GetProperty("layout").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(layoutId));

        var bump = await client.PostAsJsonAsync(
            "v1/agent/tools/orbit_save_layout",
            new { layoutId, name = "Lanes", schemaJson = """{"lanes":[{"id":"one"},{"id":"two"}]}""" });
        bump.EnsureSuccessStatusCode();

        var revert = await client.PostAsJsonAsync(
            "v1/agent/tools/orbit_revert_layout",
            new { layoutId });
        revert.EnsureSuccessStatusCode();
        await using var revertStream = await revert.Content.ReadAsStreamAsync();
        using var revertDoc = await JsonDocument.ParseAsync(revertStream);
        var schema = revertDoc.RootElement.GetProperty("layout").GetProperty("schemaJson").GetString() ?? string.Empty;
        Assert.DoesNotContain("\"id\":\"two\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevCreateBranch_Telegram_IsForbidden()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_dev_create_branch",
            new
            {
                branchName = "feat/remote",
                provenance = new { channel = "telegram" },
            });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DevWriteFile_OutsideRepo_IsForbidden()
    {
        var outside = Path.Combine(_tempRoot!, "project-folder", "x.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_dev_write_file",
            new { path = outside, contents = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
