using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Diagnostics;

namespace Orbit.Core.Host.Api;

public sealed class DiagnosticsExportRequest
{
    /// <summary>json (default) or zip.</summary>
    public string? Format { get; set; }
}

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Diagnostics, GetDiagnostics);
        app.MapPost(HostEndpoints.DiagnosticsExport, ExportDiagnostics);
        return app;
    }

    private static IResult GetDiagnostics(DiagnosticsBundleBuilder builder, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var report = builder.Build();
        return Results.Json(new
        {
            diagnostics = report,
            requestId,
        });
    }

    private static IResult ExportDiagnostics(
        DiagnosticsExportRequest? body,
        DiagnosticsBundleBuilder builder,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var format = string.IsNullOrWhiteSpace(body?.Format) ? "json" : body!.Format!.Trim().ToLowerInvariant();
        try
        {
            string path;
            if (format is "zip")
            {
                path = builder.WriteZipExport();
            }
            else if (format is "json")
            {
                path = builder.WriteJsonExport();
            }
            else
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Format must be 'json' or 'zip'.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Json(new
            {
                path,
                format,
                requestId,
                redactions = new[] { "apiKeys", "hermesKeyFileContents", "emailBodies", "coreHostApiKey" },
            });
        }
        catch (Exception ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }
}
