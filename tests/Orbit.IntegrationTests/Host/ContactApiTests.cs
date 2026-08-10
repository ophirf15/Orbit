using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Hosting;
using Orbit.Infrastructure.Contacts;
using Orbit.Infrastructure.Data;

namespace Orbit.IntegrationTests.HostApi;

public sealed class ContactApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private ContactStore? _contacts;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitContactIT", Guid.NewGuid().ToString("N"));
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
        _contacts = new ContactStore(factory);

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
    public async Task ListDetailAndUpdateContact()
    {
        var personId = _contacts!.UpsertPersonByEmail(
            "alex.rivera@metrofiber.example",
            "Alex Rivera",
            sourceEmailId: null,
            ContactSourceKinds.UserUpdate);

        using var list = await _client!.GetAsync("v1/contacts");
        list.EnsureSuccessStatusCode();
        await using var listStream = await list.Content.ReadAsStreamAsync();
        using var listDoc = await JsonDocument.ParseAsync(listStream);
        Assert.Contains(
            listDoc.RootElement.GetProperty("contacts").EnumerateArray(),
            e => e.GetProperty("id").GetString() == personId);

        using var detail = await _client.GetAsync($"v1/contacts/{personId}");
        detail.EnsureSuccessStatusCode();

        var update = await _client.PostAsJsonAsync(
            $"v1/contacts/{personId}",
            new
            {
                patch = new { mobile = "415-555-0198" },
                provenance = "Add mobile via UpdateContact",
                requestedBy = "user",
            });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        await using var updateStream = await update.Content.ReadAsStreamAsync();
        using var updateDoc = await JsonDocument.ParseAsync(updateStream);
        var methods = updateDoc.RootElement.GetProperty("methods").EnumerateArray().ToList();
        Assert.Contains(methods, m =>
            m.GetProperty("methodType").GetString() == "mobile"
            && (m.GetProperty("value").GetString() ?? string.Empty).Contains("555", StringComparison.Ordinal));

        using var orgs = await _client.GetAsync("v1/organizations");
        orgs.EnsureSuccessStatusCode();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
