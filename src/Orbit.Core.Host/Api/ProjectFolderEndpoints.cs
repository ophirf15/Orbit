using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Host.Security;
using Orbit.Infrastructure.Files;

namespace Orbit.Core.Host.Api;

public sealed class AttachFolderRequest
{
    public string? Path { get; set; }
}

public sealed class FileLinkRequest
{
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? ProjectId { get; set; }
}

public static class ProjectFolderEndpoints
{
    public static IEndpointRouteBuilder MapProjectFolderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/projects/{projectId}/folders", (string projectId, ProjectFolderStore folders, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var items = folders.ListForProject(projectId);
            return Results.Json(new
            {
                folders = items.Select(f => new
                {
                    id = f.Id,
                    projectId = f.ProjectId,
                    rootPath = f.RootPath,
                    availability = f.Availability,
                    lastIndexedAt = f.LastIndexedAt,
                    isHome = f.IsHome,
                }),
                requestId,
            });
        });

        app.MapPost("/v1/projects/{projectId}/home-folder", (
            string projectId,
            AttachFolderRequest body,
            ProjectFolderStore folders,
            FileIndexService index,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var folder = folders.SetHome(projectId, body.Path ?? string.Empty);
                var indexed = index.ReindexFolderDetailed(folder.Id);
                hub.Publish(new OrbitEvent
                {
                    Type = "folder.home_set",
                    Payload = new { folderId = folder.Id, projectId, indexed = indexed.TouchedCount, rootPath = folder.RootPath },
                });
                return Results.Json(new
                {
                    id = folder.Id,
                    projectId = folder.ProjectId,
                    rootPath = folder.RootPath,
                    availability = folder.Availability,
                    isHome = folder.IsHome,
                    indexedCount = indexed.TouchedCount,
                    orbitSandboxPath = OrbitHomeSandbox.GetSandboxRoot(folder.RootPath),
                    reindex = ToReindexPayload(indexed),
                    requestId,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
        });

        app.MapGet("/v1/projects/{projectId}/home-folder", (string projectId, ProjectFolderStore folders, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var home = folders.GetHome(projectId);
            if (home is null)
            {
                return Results.Json(new { home = (object?)null, requestId });
            }

            return Results.Json(new
            {
                home = new
                {
                    id = home.Id,
                    projectId = home.ProjectId,
                    rootPath = home.RootPath,
                    availability = home.Availability,
                    lastIndexedAt = home.LastIndexedAt,
                    isHome = home.IsHome,
                    orbitSandboxPath = OrbitHomeSandbox.GetSandboxRoot(home.RootPath),
                },
                requestId,
            });
        });

        app.MapPost("/v1/projects/{projectId}/folders", (
            string projectId,
            AttachFolderRequest body,
            ProjectFolderStore folders,
            FileIndexService index,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var folder = folders.Attach(projectId, body.Path ?? string.Empty);
                var indexed = index.ReindexFolderDetailed(folder.Id);
                hub.Publish(new OrbitEvent
                {
                    Type = "folder.attached",
                    Payload = new { folderId = folder.Id, projectId, indexed = indexed.TouchedCount },
                });
                return Results.Json(new
                {
                    id = folder.Id,
                    projectId = folder.ProjectId,
                    rootPath = folder.RootPath,
                    availability = folder.Availability,
                    isHome = folder.IsHome,
                    indexedCount = indexed.TouchedCount,
                    reindex = ToReindexPayload(indexed),
                    requestId,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (DirectoryNotFoundException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
        });

        app.MapPost("/v1/projects/{projectId}/folders/{folderId}/reindex", (
            string projectId,
            string folderId,
            ProjectFolderStore folders,
            FileIndexService index,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var folder = folders.Get(folderId);
            if (folder is null || !string.Equals(folder.ProjectId, projectId, StringComparison.Ordinal))
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, "Folder was not found.", requestId), statusCode: 404);
            }

            var includeOffline = true;
            if (http.Request.Query.TryGetValue("includeOfflinePlaceholders", out var raw)
                && bool.TryParse(raw.ToString(), out var parsed))
            {
                includeOffline = parsed;
            }

            var indexed = index.ReindexFolderDetailed(
                folderId,
                new FileReindexOptions { IncludeOfflinePlaceholders = includeOffline });
            return Results.Json(new
            {
                folderId,
                indexedCount = indexed.TouchedCount,
                reindex = ToReindexPayload(indexed),
                requestId,
            });
        });

        app.MapGet(HostEndpoints.FilesSearch, (string? q, string? projectId, FileIndexService index, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            if (string.IsNullOrWhiteSpace(q))
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide q and/or projectId.", requestId),
                        statusCode: 400);
                }

                var listed = index.ListForProject(projectId);
                return Results.Json(new
                {
                    results = listed.Select(h => new
                    {
                        id = h.Id,
                        path = h.Path,
                        displayName = h.DisplayName,
                        extension = h.Extension,
                        snippet = h.Snippet,
                        projectId = h.ProjectId,
                    }),
                    requestId,
                });
            }

            var hits = index.Search(q, projectId);
            return Results.Json(new
            {
                results = hits.Select(h => new
                {
                    id = h.Id,
                    path = h.Path,
                    displayName = h.DisplayName,
                    extension = h.Extension,
                    snippet = h.Snippet,
                    projectId = h.ProjectId,
                }),
                requestId,
            });
        });

        app.MapGet("/v1/files/{fileId}", (string fileId, FileIndexService index, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var file = index.Get(fileId);
            if (file is null)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, "File was not found.", requestId), statusCode: 404);
            }

            var links = index.ListLinks(fileId);
            return Results.Json(new
            {
                id = file.Id,
                path = file.Path,
                displayName = file.DisplayName,
                extension = file.Extension,
                sizeBytes = file.SizeBytes,
                modifiedAt = file.ModifiedAt,
                contentHash = file.ContentHash,
                mimeType = file.MimeType,
                availability = file.Availability,
                previewText = file.IndexedTextPreview,
                links = links.Select(l => new { entityType = l.EntityType, entityId = l.EntityId }),
                requestId,
            });
        });

        app.MapPost("/v1/files/{fileId}/preview", (string fileId, FileIndexService index, IExternalFileCapability external, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var file = index.Get(fileId);
            if (file is null)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, "File was not found.", requestId), statusCode: 404);
            }

            string? livePreview = null;
            try
            {
                livePreview = external.ReadTextPreview(file.Path);
            }
            catch (Exception)
            {
                livePreview = file.IndexedTextPreview;
            }

            return Results.Json(new
            {
                id = file.Id,
                path = file.Path,
                displayName = file.DisplayName,
                previewText = livePreview ?? file.IndexedTextPreview,
                requestId,
            });
        });

        app.MapPost("/v1/files/{fileId}/open", (string fileId, FileIndexService index, IExternalFileCapability external, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var file = index.Get(fileId);
            if (file is null)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, "File was not found.", requestId), statusCode: 404);
            }

            try
            {
                external.OpenExternally(file.Path);
                return Results.Json(new { id = file.Id, path = file.Path, opened = true, requestId });
            }
            catch (Exception ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
        });

        app.MapPost("/v1/files/{fileId}/links", (string fileId, FileLinkRequest body, FileIndexService index, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (!string.IsNullOrWhiteSpace(body.ProjectId))
                {
                    index.LinkToProject(fileId, body.ProjectId);
                }

                if (!string.IsNullOrWhiteSpace(body.EntityType) && !string.IsNullOrWhiteSpace(body.EntityId))
                {
                    index.LinkToEntity(fileId, body.EntityType, body.EntityId);
                }

                if (string.IsNullOrWhiteSpace(body.ProjectId)
                    && (string.IsNullOrWhiteSpace(body.EntityType) || string.IsNullOrWhiteSpace(body.EntityId)))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide projectId and/or entityType+entityId.", requestId),
                        statusCode: 400);
                }

                return Results.Json(new
                {
                    fileId,
                    links = index.ListLinks(fileId).Select(l => new { entityType = l.EntityType, entityId = l.EntityId }),
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
        });

        // Explicit deny surface — external mutation must fail by construction.
        app.MapMethods("/v1/files/external/delete", ["POST", "DELETE"], (HttpContext http) =>
            DenyExternalMutation(http));
        app.MapMethods("/v1/files/external/rename", ["POST"], (HttpContext http) =>
            DenyExternalMutation(http));
        app.MapMethods("/v1/files/external/move", ["POST"], (HttpContext http) =>
            DenyExternalMutation(http));
        app.MapMethods("/v1/files/external/write", ["POST", "PUT"], (HttpContext http) =>
            DenyExternalMutation(http));

        return app;
    }

    private static object ToReindexPayload(FileReindexResult indexed) => new
    {
        indexedCount = indexed.TouchedCount,
        skippedUnchangedCount = indexed.SkippedUnchangedCount,
        extractedCount = indexed.ExtractedCount,
        offlinePlaceholderCount = indexed.OfflinePlaceholderCount,
        softSkippedDirectoryCount = indexed.SoftSkippedDirectoryCount,
        softSkippedDirectories = indexed.SoftSkippedDirectories,
        sampleRelativePaths = indexed.SampleRelativePaths,
        warning = indexed.Warning,
    };

    private static IResult DenyExternalMutation(HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(
            ApiErrors.Create(
                ApiErrorCodes.PathDenied,
                "External/project files are read-only. Delete, rename, move, and overwrite are not available.",
                requestId),
            statusCode: StatusCodes.Status403Forbidden);
    }
}
