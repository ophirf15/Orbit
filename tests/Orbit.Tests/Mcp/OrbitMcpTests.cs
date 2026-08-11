using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using ModelContextProtocol.Server;
using Orbit.Mcp;

namespace Orbit.Tests.Mcp;

public sealed class OrbitToolCatalogTests
{
    [Fact]
    public void Catalog_lists_allowlisted_tools()
    {
        Assert.Equal(
            [
                "orbit_get_related_context",
                "orbit_search",
                "orbit_get_project",
                "orbit_get_contact",
                "orbit_update_contact",
                "orbit_list_contacts",
                "orbit_archive_contact",
                "orbit_flag_resident",
                "orbit_get_workbench",
                "orbit_get_calendar_context",
                "orbit_create_project",
                "orbit_create_workstream",
                "orbit_list_workstreams",
                "orbit_create_task",
                "orbit_update_task",
                "orbit_update_project",
                "orbit_merge_project",
                "orbit_add_project_alias",
                "orbit_remove_project_alias",
                "orbit_list_project_aliases",
                "orbit_create_note",
                "orbit_archive_entity",
                "orbit_link_tasks",
                "orbit_unlink_tasks",
                "orbit_get_task_dependencies",
                "orbit_suggest_task_links",
                "orbit_accept_suggestion",
                "orbit_reject_suggestion",
                "orbit_remember",
                "orbit_forget",
                "orbit_list_rules",
                "orbit_set_rule",
                "orbit_list_memory",
                "orbit_report_briefing",
                "orbit_link_email_thread",
                "orbit_list_task_emails",
                "orbit_open_email",
                "orbit_get_changes",
                "orbit_get_pulse_delta",
                "orbit_list_blocked_tasks",
                "orbit_get_agent_snapshot",
                "orbit_health",
            ],
            OrbitToolCatalog.All.ToArray());
    }

    [Fact]
    public void Mcp_tool_attributes_match_catalog_without_network()
    {
        var named = typeof(OrbitMcpTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var expected = OrbitToolCatalog.All.OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, named);
    }
}

public sealed class OrbitCoreClientTests
{
    [Fact]
    public async Task CallToolAsync_posts_to_agent_tool_route_with_bearer()
    {
        var handler = new ScriptedHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/agent/tools/orbit_search", request.RequestUri?.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"hits":[]}""", Encoding.UTF8, "application/json"),
            };
        });

        using var http = new HttpClient(handler);
        OrbitCoreClient.ConfigureHttpClient(http, new OrbitCoreOptions
        {
            BaseUrl = "http://127.0.0.1:8741",
            ApiKey = "test-key",
        });

        var client = new OrbitCoreClient(http);
        var body = await client.CallToolAsync("orbit_search", new { q = "Harbor Court" });
        Assert.Contains("hits", body, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _handler;

        public int CallCount { get; private set; }

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_handler(request, cancellationToken));
        }
    }
}
