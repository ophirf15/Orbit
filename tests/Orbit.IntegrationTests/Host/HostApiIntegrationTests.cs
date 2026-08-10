using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;

namespace Orbit.IntegrationTests.HostApi;

public sealed class HostApiIntegrationTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitHostIT", Guid.NewGuid().ToString("N"));
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
    public async Task Health_ReturnsOk()
    {
        var response = await _client!.GetAsync("v1/health");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.Contains("X-Request-Id"));
    }

    [Fact]
    public async Task FilesWrite_DeniesExternalPath()
    {
        var external = Path.Combine(_tempRoot!, "outside.txt");
        var response = await _client!.PostAsJsonAsync("v1/files/write", new { path = external, content = "nope" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("path_denied", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(external));
    }

    [Fact]
    public async Task FilesWrite_AllowsGeneratedChild()
    {
        var target = Path.Combine(_options!.GeneratedFilesRoot, "artifacts", "ok.txt");
        var response = await _client!.PostAsJsonAsync("v1/files/write", new { path = target, content = "hello" });
        response.EnsureSuccessStatusCode();
        Assert.True(File.Exists(target));
        Assert.Equal("hello", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task SqlAndShell_RoutesDoNotExist()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client!.GetAsync("v1/sql")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("v1/shell")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("sql", null)).StatusCode);
    }

    [Fact]
    public async Task Events_SendsConnectedFrame()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await _client!.GetAsync("v1/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var line1 = await reader.ReadLineAsync(cts.Token);
        var line2 = await reader.ReadLineAsync(cts.Token);
        Assert.Equal("event: orbit", line1);
        Assert.NotNull(line2);
        Assert.Contains("connected", line2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonLoopback_WithoutKey_Refuses()
    {
        var options = new HostOptions { BindAddress = "0.0.0.0", ApiKey = null };
        Assert.Throws<InvalidOperationException>(() => HostStartupGuard.EnsureMayListen(options));
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
