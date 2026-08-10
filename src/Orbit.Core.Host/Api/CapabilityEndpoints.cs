using Orbit.Core.Host;
using Orbit.Core.Host.Auth;

namespace Orbit.Core.Host.Api;

public static class CapabilityEndpoints
{
    public static IEndpointRouteBuilder MapCapabilityStubEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Projects, (Orbit.Infrastructure.Data.ProjectReadStore projects, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var items = projects.ListActive();
            return Results.Json(new { projects = items, requestId });
        });

        // Detail GET is replaced by /context in WorkbenchEndpoints; PATCH is real (name/summary).
        // POST create is MapWorkbenchEndpoints.
        MapStubMethods(app, $"{HostEndpoints.Projects}/{{id}}", ["PUT", "DELETE"]);
        MapStubMethods(app, HostEndpoints.Projects, ["PUT", "PATCH", "DELETE"]);
        MapStub(app, HostEndpoints.Tasks);
        MapStubMethods(app, HostEndpoints.Notes, ["PUT", "DELETE"]);
        MapStubMethods(app, HostEndpoints.NoteById, ["GET", "PUT", "DELETE"]);
        // Search + evidence live on SearchEndpoints (Phase 16).
        MapStubMethods(app, HostEndpoints.Contacts, ["DELETE", "PUT"]);
        MapStub(app, HostEndpoints.Links);
        // Calendar GET/POST live on CalendarEndpoints.
        // Suggestions GET/accept/reject are MapSuggestionEndpoints; reserve other verbs.
        MapStubMethods(app, HostEndpoints.Suggestions, ["POST", "PUT", "PATCH", "DELETE"]);
        return app;
    }

    private static void MapStub(IEndpointRouteBuilder app, string pattern, bool excludeGet = false)
    {
        var methods = excludeGet
            ? new[] { "POST", "PUT", "PATCH", "DELETE" }
            : new[] { "GET", "POST", "PUT", "PATCH", "DELETE" };
        MapStubMethods(app, pattern, methods);
    }

    private static void MapStubMethods(IEndpointRouteBuilder app, string pattern, string[] methods)
    {
        app.MapMethods(pattern, methods, (HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            return Results.Json(
                ApiErrors.Create(
                    ApiErrorCodes.NotImplemented,
                    "Capability write/detail persistence arrives in a later phase. Route is reserved.",
                    requestId),
                statusCode: StatusCodes.Status501NotImplemented);
        });
    }
}
