using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Sync;

namespace Orbit.Core.Host.Api;

public sealed class SyncRestoreRequest
{
    public string? SnapshotId { get; set; }
}

public static class SyncEndpoints
{
    public static IEndpointRouteBuilder MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(HostEndpoints.SyncSnapshot, CreateSnapshot);
        app.MapGet(HostEndpoints.SyncSnapshots, ListSnapshots);
        app.MapPost(HostEndpoints.SyncRestore, Restore);
        app.MapGet(HostEndpoints.SyncStatus, GetStatus);
        return app;
    }

    private static IResult CreateSnapshot(SnapshotService sync, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var manifest = sync.CreateSnapshot();
            return Results.Json(new
            {
                snapshot = ToDto(manifest),
                status = ToStatusDto(sync.GetStatus()),
                requestId,
            });
        }
        catch (InvalidOperationException ex)
        {
            // Missing/offline folder must not look like a hard server fault for UI.
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (Exception ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult ListSnapshots(SnapshotService sync, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var list = sync.ListSnapshots();
            return Results.Json(new
            {
                snapshots = list.Select(ToDto),
                requestId,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(new
            {
                snapshots = Array.Empty<object>(),
                warning = ex.Message,
                requestId,
            });
        }
    }

    private static IResult Restore(SyncRestoreRequest? body, SnapshotService sync, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.SnapshotId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'snapshotId' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var manifest = sync.RestoreSnapshot(body.SnapshotId.Trim(), allowDuringConflict: true);
            return Results.Json(new
            {
                snapshot = ToDto(manifest),
                status = ToStatusDto(sync.GetStatus()),
                requestId,
            });
        }
        catch (Exception ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult GetStatus(SnapshotService sync, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var status = sync.GetStatus();
        return Results.Json(new
        {
            status = ToStatusDto(status),
            requestId,
        });
    }

    private static object ToDto(SnapshotManifest m) => new
    {
        snapshotId = m.SnapshotId,
        schemaVersion = m.SchemaVersion,
        revision = m.Revision,
        parentRevision = m.ParentRevision,
        deviceId = m.DeviceId,
        deviceName = m.DeviceName,
        createdAt = m.CreatedAt,
        checksumSha256 = m.ChecksumSha256,
    };

    private static object ToStatusDto(SyncStatus s) => new
    {
        kind = s.Kind.ToString(),
        message = s.Message,
        syncFolder = s.SyncFolder,
        localRevision = s.LocalRevision,
        latestCloudRevision = s.LatestCloudRevision,
        latestCloudSnapshotId = s.LatestCloudSnapshotId,
        localDirty = s.LocalDirty,
        lastSnapshotAt = s.LastSnapshotAt,
        deviceId = s.DeviceId,
        continueFromBackupAvailable = s.ContinueFromBackupAvailable,
        autoBackupHint = s.AutoBackupHint,
        conflict = s.Conflict is null
            ? null
            : new
            {
                kind = s.Conflict.Kind.ToString(),
                message = s.Conflict.Message,
                localRevision = s.Conflict.LocalRevision,
                cloudRevision = s.Conflict.CloudRevision,
                cloudSnapshotId = s.Conflict.CloudSnapshotId,
            },
    };
}
