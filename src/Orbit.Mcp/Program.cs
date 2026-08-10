using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Orbit.Mcp;

var builder = Host.CreateApplicationBuilder(args);

// MCP stdio uses stdout for JSON-RPC — keep logs on stderr only.
// Clear default providers (EventLog binding breaks framework-dependent copies).
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

var coreOptions = OrbitCoreOptions.FromEnvironment();
builder.Services.AddSingleton(Options.Create(coreOptions));
builder.Services.AddHttpClient<OrbitCoreClient>((_, client) =>
    OrbitCoreClient.ConfigureHttpClient(client, coreOptions));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "orbit-core-tools",
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
        };
    })
    .WithStdioServerTransport()
    .WithTools<OrbitMcpTools>();

await builder.Build().RunAsync().ConfigureAwait(false);
