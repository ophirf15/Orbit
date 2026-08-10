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

public static class SuggestionEndpoints
{
    public static IEndpointRouteBuilder MapSuggestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Suggestions, ListSuggestions);
        app.MapGet($"{HostEndpoints.Suggestions}/{{id}}", GetSuggestion);
        app.MapPost($"{HostEndpoints.Suggestions}/{{id}}/accept", AcceptSuggestion);
        app.MapPost($"{HostEndpoints.Suggestions}/{{id}}/reject", RejectSuggestion);
        return app;
    }

    private static IResult ListSuggestions(string? status, SuggestionStore store, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var effective = string.IsNullOrWhiteSpace(status) ? SuggestionStatuses.Pending : status;
            var items = store.List(effective);
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
        status = s.Status,
        confidence = s.Confidence,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
    };
}
