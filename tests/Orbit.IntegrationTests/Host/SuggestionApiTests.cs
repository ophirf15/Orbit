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
using Orbit.Infrastructure.Suggestions;

namespace Orbit.IntegrationTests.HostApi;

public sealed class SuggestionApiTests : IAsyncLifetime
{
    private WebApplication? _app;
    private HostOptions? _options;
    private string? _tempRoot;
    private HttpClient? _client;
    private string? _projectId;
    private string? _contactId;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitSuggestionIT", Guid.NewGuid().ToString("N"));
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
        _contactId = ids.ContactPersonId;

        await _app.StartAsync();
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
    public async Task CaptureMatchingProject_AutoAssignsViaWorker()
    {
        const string text = "Ping Harbor Court electrician";
        var create = await _client!.PostAsJsonAsync(
            "v1/notes",
            new { text, projectId = (string?)null });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        await using var createdStream = await create.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var noteId = createdDoc.RootElement.GetProperty("noteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(noteId));

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(_options!.LocalDataRoot));
        string? projectId = null;
        var isLimbo = 1;
        string? originalText = null;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            using var connection = factory.CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT project_id, is_limbo, original_text FROM notes WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", noteId!);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            projectId = reader.IsDBNull(0) ? null : reader.GetString(0);
            isLimbo = reader.GetInt32(1);
            originalText = reader.GetString(2);
            if (!string.IsNullOrWhiteSpace(projectId) && isLimbo == 0)
            {
                break;
            }
        }

        Assert.Equal(_projectId, projectId);
        Assert.Equal(0, isLimbo);
        Assert.Equal(text, originalText);
    }

    [Fact]
    public async Task AcceptPendingSuggestion_AppliesAssignment()
    {
        var create = await _client!.PostAsJsonAsync(
            "v1/notes",
            new { text = "Manual assign candidate", projectId = (string?)null });
        create.EnsureSuccessStatusCode();
        await using var createdStream = await create.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var noteId = createdDoc.RootElement.GetProperty("noteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(noteId));

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(_options!.LocalDataRoot));
        var suggestions = new SuggestionStore(factory);
        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.AssignToProject,
            Summary = "Assign to Harbor Court",
            PayloadJson = "{\"action\":\"assign_to_project\",\"noteId\":\"" + noteId
                + "\",\"projectId\":\"" + _projectId + "\"}",
            NoteId = noteId,
            ProjectId = _projectId,
            Confidence = 0.55,
        });

        var accept = await _client.PostAsJsonAsync(
            $"v1/suggestions/{suggestion.Id}/accept",
            new { actor = "user" });
        accept.EnsureSuccessStatusCode();

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id, is_limbo, original_text FROM notes WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", noteId!);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(_projectId, reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal("Manual assign candidate", reader.GetString(2));
    }

    [Fact]
    public async Task RejectSuggestion_SetsRejected()
    {
        var create = await _client!.PostAsJsonAsync(
            "v1/notes",
            new { text = "Unrelated capture xyzzy", projectId = (string?)null });
        create.EnsureSuccessStatusCode();
        await using var createdStream = await create.Content.ReadAsStreamAsync();
        using var createdDoc = await JsonDocument.ParseAsync(createdStream);
        var noteId = createdDoc.RootElement.GetProperty("noteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(noteId));

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(_options!.LocalDataRoot));
        var suggestions = new SuggestionStore(factory);
        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.AssignToProject,
            Summary = "Maybe assign",
            PayloadJson = "{\"action\":\"assign_to_project\",\"noteId\":\"" + noteId
                + "\",\"projectId\":\"" + _projectId + "\"}",
            NoteId = noteId,
            ProjectId = _projectId,
            Confidence = 0.5,
        });

        var reject = await _client.PostAsJsonAsync(
            $"v1/suggestions/{suggestion.Id}/reject",
            new { actor = "user" });
        reject.EnsureSuccessStatusCode();
        await using var rejectStream = await reject.Content.ReadAsStreamAsync();
        using var rejectDoc = await JsonDocument.ParseAsync(rejectStream);
        Assert.Equal("rejected", rejectDoc.RootElement.GetProperty("suggestion").GetProperty("status").GetString());
    }

    [Fact]
    public async Task OrbitUpdateContactTool_WritesAudit()
    {
        var response = await _client!.PostAsJsonAsync(
            "v1/agent/tools/orbit_update_contact",
            new
            {
                id = _contactId,
                patch = new { phone = "+1-555-0199" },
                provenance = "Phase10 tool test",
                actor = "agent",
            });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("orbit_update_contact", body, StringComparison.OrdinalIgnoreCase);

        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(_options!.LocalDataRoot));
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM audit_events WHERE event_type = 'contact.updated' AND entity_id = $id;";
        cmd.Parameters.AddWithValue("$id", _contactId!);
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 1);
    }

    [Fact]
    public async Task ExternalDelete_StillForbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _client!.PostAsync("v1/files/external/delete", null)).StatusCode);
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
