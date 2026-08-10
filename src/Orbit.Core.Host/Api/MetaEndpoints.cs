using System.Diagnostics;
using System.Reflection;
using Orbit.Agent.Contracts.Capabilities;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;

namespace Orbit.Core.Host.Api;

public static class MetaEndpoints
{
    private static readonly Stopwatch Uptime = Stopwatch.StartNew();

    public static IEndpointRouteBuilder MapMetaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Health, (HttpContext http) =>
        {
            return Results.Json(new
            {
                status = "ok",
                uptimeSeconds = Uptime.Elapsed.TotalSeconds,
                features = new[]
                {
                    "project-board",
                    "task-email-threads",
                    "task-dependencies",
                    "duty-operator",
                    "pulse",
                    "orbit-ignition",
                },
                requestId = ApiKeyMiddleware.GetRequestId(http),
            });
        });

        app.MapGet(HostEndpoints.Version, (HttpContext http) =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "0.0.0";
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            return Results.Json(new
            {
                name = "Orbit.Core.Host",
                version,
                informationalVersion = informational,
                targetFramework = "net9.0",
                requestId = ApiKeyMiddleware.GetRequestId(http),
            });
        });

        app.MapGet(HostEndpoints.Capabilities, (HttpContext http) =>
        {
            return Results.Json(new
            {
                capabilities = CapabilityCatalog.All,
                requestId = ApiKeyMiddleware.GetRequestId(http),
            });
        });

        return app;
    }
}
