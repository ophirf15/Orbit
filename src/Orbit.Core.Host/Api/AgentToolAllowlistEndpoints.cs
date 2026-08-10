using Orbit.Core.Host;
using Orbit.Core.Host.Auth;

namespace Orbit.Core.Host.Api;

/// <summary>
/// Catch-all for unknown agent tool names — allowlist is explicit mapped routes only.
/// Registered after concrete tool endpoints so literals win.
/// </summary>
public static class AgentToolAllowlistEndpoints
{
    public static IEndpointRouteBuilder MapUnknownAgentToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods(
            "/v1/agent/tools/{toolName}",
            ["GET", "POST", "PUT", "PATCH", "DELETE"],
            (string toolName, HttpContext http) =>
            {
                var requestId = ApiKeyMiddleware.GetRequestId(http);
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.NotFound,
                        $"Unknown or disallowlisted agent tool '{toolName}'.",
                        requestId),
                    statusCode: StatusCodes.Status404NotFound);
            });

        return app;
    }
}
