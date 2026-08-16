using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Data;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Core.Host.Api;

public sealed class SuggestionDecisionRequest
{
    public string? Actor { get; set; }

    /// <summary>Required when accepting <c>disambiguate_email_claim</c> without payload.projectId.</summary>
    public string? ApplyProjectId { get; set; }
}

public sealed class SuggestionBatchDecideRequest
{
    public string[]? Ids { get; set; }

    /// <summary>accept | reject | expire</summary>
    public string? Decision { get; set; }

    public string? Actor { get; set; }

    public string? ApplyProjectId { get; set; }
}

public static class SuggestionEndpoints
{
    public static IEndpointRouteBuilder MapSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Suggestions, ListSuggestions);
        app.MapPost(HostEndpoints.SuggestionsBatchDecide, BatchDecideSuggestions);
        app.MapGet($"{HostEndpoints.Suggestions}/{{id}}", GetSuggestion);
        app.MapPost($"{HostEndpoints.Suggestions}/{{id}}/accept", AcceptSuggestion);
        app.MapPost($"{HostEndpoints.Suggestions}/{{id}}/reject", RejectSuggestion);
        return app;
    }

    private static IResult ListSuggestions(
        string? status,
        string? projectId,
        double? minConfidence,
        double? maxConfidence,
        string? queue,
        SuggestionStore store,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            // Lightweight hygiene: age out stale pending on read paths.
            if (string.IsNullOrWhiteSpace(status)
                || string.Equals(status, SuggestionStatuses.Pending, StringComparison.Ordinal)
                || !string.IsNullOrWhiteSpace(queue))
            {
                store.ExpireOlderThan(SuggestionHygiene.DefaultExpireAge);
            }

            var effective = string.IsNullOrWhiteSpace(queue)
                ? (string.IsNullOrWhiteSpace(status) ? SuggestionStatuses.Pending : status)
                : null;
            var items = store.List(
                status: effective,
                projectId: projectId,
                minConfidence: minConfidence,
                maxConfidence: maxConfidence,
                queue: queue);
            return Results.Json(new
            {
                suggestions = items.Select(Map),
                requestId,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult BatchDecideSuggestions(
        SuggestionBatchDecideRequest? body,
        SuggestionStore store,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body?.Ids is null || body.Ids.Length == 0)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "ids are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(body.Decision))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "decision is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var results = store.BatchDecide(body.Ids, body.Decision, body.Actor, body.ApplyProjectId);
            foreach (var item in results.Where(r => r.Ok && r.Suggestion is not null))
            {
                var decision = body.Decision.Trim().ToLowerInvariant();
                hub.Publish(new OrbitEvent
                {
                    Type = decision switch
                    {
                        "accept" => "suggestion.accepted",
                        "reject" => "suggestion.rejected",
                        "expire" => "suggestion.expired",
                        _ => "suggestion.decided",
                    },
                    Payload = new
                    {
                        suggestionId = item.Id,
                        noteId = item.AppliedNoteId ?? item.Suggestion!.NoteId,
                        projectId = item.AppliedProjectId ?? item.Suggestion!.ProjectId,
                        taskId = item.CreatedTaskId,
                    },
                });
            }

            return Results.Json(new
            {
                results = results.Select(r => new
                {
                    id = r.Id,
                    ok = r.Ok,
                    error = r.Error,
                    suggestion = r.Suggestion is null ? null : Map(r.Suggestion),
                    appliedNoteId = r.AppliedNoteId,
                    appliedProjectId = r.AppliedProjectId,
                    createdTaskId = r.CreatedTaskId,
                }),
                accepted = results.Count(r => r.Ok
                    && string.Equals(r.Suggestion?.Status, SuggestionStatuses.Accepted, StringComparison.Ordinal)),
                rejected = results.Count(r => r.Ok
                    && string.Equals(r.Suggestion?.Status, SuggestionStatuses.Rejected, StringComparison.Ordinal)),
                expired = results.Count(r => r.Ok
                    && string.Equals(r.Suggestion?.Status, SuggestionStatuses.Expired, StringComparison.Ordinal)),
                failed = results.Count(r => !r.Ok),
                requestId,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult GetSuggestion(string id, SuggestionStore store, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var item = store.Get(id);
        if (item is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Suggestion was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { suggestion = Map(item), requestId });
    }

    private static IResult AcceptSuggestion(
        string id,
        SuggestionDecisionRequest? body,
        SuggestionStore store,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var result = store.Accept(id, body?.Actor, applyProjectId: body?.ApplyProjectId);
            hub.Publish(new OrbitEvent
            {
                Type = "suggestion.accepted",
                Payload = new
                {
                    suggestionId = result.Suggestion.Id,
                    noteId = result.AppliedNoteId,
                    projectId = result.AppliedProjectId,
                    taskId = result.CreatedTaskId,
                },
            });

            return Results.Json(new
            {
                suggestion = Map(result.Suggestion),
                appliedNoteId = result.AppliedNoteId,
                appliedProjectId = result.AppliedProjectId,
                createdTaskId = result.CreatedTaskId,
                requestId,
            });
        }
        catch (ArgumentException ex)
        {
            var status = ex.ParamName is "id" or "projectId" or "noteId"
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: status);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult RejectSuggestion(
        string id,
        SuggestionDecisionRequest? body,
        SuggestionStore store,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var result = store.Reject(id, body?.Actor);
            hub.Publish(new OrbitEvent
            {
                Type = "suggestion.rejected",
                Payload = new { suggestionId = result.Id, noteId = result.NoteId },
            });

            return Results.Json(new { suggestion = Map(result), requestId });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static object Map(AgentSuggestionRecord s) => new
    {
        id = s.Id,
        suggestionType = s.SuggestionType,
        summary = s.Summary,
        payloadJson = s.PayloadJson,
        projectId = s.ProjectId,
        workstreamId = s.WorkstreamId,
        taskId = s.TaskId,
        noteId = s.NoteId,
        groupKey = s.GroupKey,
        status = s.Status,
        confidence = s.Confidence,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
    };
}
