using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Host.Security;

namespace Orbit.Core.Host.Api;

public sealed class FilePathRequest
{
    public string? Path { get; set; }

    public string? Content { get; set; }
}

public sealed class ArtifactCreateRequest
{
    public string? RelativePath { get; set; }

    public string? Content { get; set; }
}

public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(HostEndpoints.FilesRead, (FilePathRequest body, PathGuard guard, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            if (!guard.TryResolveReadable(body.Path ?? string.Empty, out var fullPath, out var error))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, error ?? "Invalid path.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!File.Exists(fullPath))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "File not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            var info = new FileInfo(fullPath);
            string? preview = null;
            try
            {
                if (info.Length <= 65_536
                    && (info.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                        || info.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                        || info.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                        || info.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                        || info.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)))
                {
                    preview = File.ReadAllText(fullPath);
                }
            }
            catch (Exception)
            {
                preview = null;
            }

            return Results.Json(new
            {
                path = fullPath,
                length = info.Length,
                lastWriteTimeUtc = info.LastWriteTimeUtc,
                contentPreview = preview,
                requestId,
            });
        });

        app.MapPost(HostEndpoints.FilesWrite, (FilePathRequest body, PathGuard guard, EventHub hub, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            if (!guard.TryResolveWritable(body.Path ?? string.Empty, out var fullPath, out var error))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.PathDenied,
                        error ?? "Write denied: path outside Orbit generated-files root.",
                        requestId),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, body.Content ?? string.Empty);
            hub.Publish(new OrbitEvent
            {
                Type = "entity.changed",
                Payload = new { kind = "file", path = fullPath },
            });

            return Results.Json(new { path = fullPath, bytes = body.Content?.Length ?? 0, requestId });
        });

        app.MapPost(HostEndpoints.Artifacts, (ArtifactCreateRequest body, PathGuard guard, EventHub hub, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var relative = string.IsNullOrWhiteSpace(body.RelativePath)
                ? Path.Combine("artifacts", $"{Guid.NewGuid():N}.txt")
                : body.RelativePath!;

            if (!guard.TryResolveWritable(relative, out var fullPath, out var error))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.PathDenied,
                        error ?? "Artifact path denied.",
                        requestId),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, body.Content ?? string.Empty);
            hub.Publish(new OrbitEvent
            {
                Type = "entity.changed",
                Payload = new { kind = "artifact", path = fullPath },
            });

            return Results.Json(new { path = fullPath, requestId });
        });

        return app;
    }
}
