using System.Text.Json;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Pulse;

namespace Orbit.Core.Host.Api;

public sealed class OrbitIgnitionFromListRequest
{
    public IReadOnlyList<string>? Names { get; set; }
}

public sealed class OrbitIgnitionFromProjectsRootRequest
{
    public string? RootPath { get; set; }
}

public static class PulseEndpoints
{
    public static IEndpointRouteBuilder MapPulseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.PulseGet, GetPulse);
        app.MapPost(HostEndpoints.PulseRefresh, RefreshPulse);
        app.MapGet(HostEndpoints.ConcernById, GetConcernById);
        app.MapGet(HostEndpoints.OrbitGet, GetOrbit);
        app.MapPost(HostEndpoints.OrbitIgnitionFromList, IgnitionFromList);
        app.MapPost(HostEndpoints.OrbitIgnitionFromProjectsRoot, IgnitionFromProjectsRoot);
        app.MapPost(HostEndpoints.OrbitIgnitionConfirm, IgnitionConfirm);
        return app;
    }

    private static IResult GetPulse(PulseReadStore pulse, OperatorRunStore runs, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            pulse = MapPulse(pulse.GetPulse(), runs.ListRecent(1).FirstOrDefault()),
            requestId,
        });
    }

    private static IResult RefreshPulse(
        PulseReadStore pulse,
        OperatorRunStore runs,
        OperatorWakeService? wake,
        EventHub? hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var payload = JsonSerializer.Serialize(new { type = "pulse.refresh", source = "host" });

        if (wake is not null)
        {
            wake.RequestWake(OperatorTriggers.DutyScan, payload);
        }
        else if (hub is not null)
        {
            hub.Publish(new OrbitEvent { Type = "pulse.refresh", Payload = new { source = "host" } });
        }

        return Results.Json(new
        {
            pulse = MapPulse(pulse.GetPulse(), runs.ListRecent(1).FirstOrDefault()),
            refreshQueued = wake is not null || hub is not null,
            requestId,
        });
    }

    private static IResult GetConcernById(
        string id,
        ProjectContextReadStore contexts,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var task = contexts.GetTask(id);
        if (task is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Concern was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new
        {
            concern = new
            {
                taskId = task.TaskId,
                projectId = task.ProjectId,
                title = task.Title,
                status = task.Status,
                nextAction = task.NextAction,
                body = task.Body,
            },
            requestId,
        });
    }

    private static IResult GetOrbit(PulseReadStore pulse, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            ignitionCompleted = pulse.IsIgnitionCompleted(),
            projects = pulse.ListOrbitProjects().Select(MapOrbitProject),
            requestId,
        });
    }

    private static IResult IgnitionFromList(
        OrbitIgnitionFromListRequest? body,
        OrbitIgnitionService ignition,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body?.Names is null || body.Names.Count == 0)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Request body 'names' must be a non-empty array.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = ignition.FromList(body.Names);
        return Results.Json(new
        {
            projects = results.Select(MapIgnitionProject),
            requestId,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult IgnitionFromProjectsRoot(
        OrbitIgnitionFromProjectsRootRequest? body,
        OrbitIgnitionService ignition,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.RootPath))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Request body 'rootPath' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var results = ignition.FromProjectsRoot(body.RootPath);
            return Results.Json(new
            {
                rootPath = Path.GetFullPath(body.RootPath.Trim()),
                projects = results.Select(MapIgnitionProject),
                requestId,
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
        }
        catch (IOException ex)
        {
            return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
        }
    }

    private static IResult IgnitionConfirm(OrbitIgnitionService ignition, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var snapshot = ignition.Confirm();
        return Results.Json(new
        {
            ignitionCompleted = true,
            snapshot = new
            {
                id = snapshot.Id,
                dayBrief = snapshot.DayBrief,
                createdAt = snapshot.CreatedAt,
            },
            requestId,
        });
    }

    private static object MapPulse(PulseView pulse, OperatorRunRecord? lastRun) => new
    {
        dayBrief = pulse.DayBrief,
        hermesHint = pulse.HermesHint,
        generatedAt = pulse.GeneratedAt,
        briefIsSynthetic = pulse.BriefIsSynthetic,
        concerns = pulse.Concerns.Select(c => new
        {
            taskId = c.TaskId,
            projectId = c.ProjectId,
            projectName = c.ProjectName,
            title = c.Title,
            status = c.Status,
            nextAction = c.NextAction,
            bodyExcerpt = c.BodyExcerpt,
            updatedAt = c.UpdatedAt,
        }),
        lastOperatorRun = lastRun is null
            ? null
            : new
            {
                id = lastRun.Id,
                triggerKind = lastRun.TriggerKind,
                status = lastRun.Status,
                briefingSummary = lastRun.BriefingSummary,
                errorText = lastRun.ErrorText,
                createdAt = lastRun.CreatedAt,
                completedAt = lastRun.CompletedAt,
            },
    };

    private static object MapOrbitProject(OrbitProjectRecord project) => new
    {
        id = project.Id,
        name = project.Name,
        summary = project.Summary,
        status = project.Status,
        inOrbit = project.InOrbit,
        openConcernCount = project.OpenConcernCount,
        topNextAction = project.TopNextAction,
    };

    private static object MapIgnitionProject(OrbitIgnitionProjectResult project) => new
    {
        id = project.Id,
        name = project.Name,
        created = project.Created,
        homeFolderPath = project.HomeFolderPath,
        error = project.Error,
    };
}
