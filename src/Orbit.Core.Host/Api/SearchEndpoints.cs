using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Search;

namespace Orbit.Core.Host.Api;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Search, Search);
        app.MapGet(HostEndpoints.EvidenceQuery, EvidenceGet);
        app.MapPost(HostEndpoints.EvidenceQuery, EvidencePost);
        return app;
    }

    private static IResult Search(
        string? q,
        string? focusProjectId,
        string? focusMeetingId,
        int? limit,
        GlobalSearchService search,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'q' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var hits = search.Search(q.Trim(), focusProjectId, focusMeetingId, limit ?? 40);
        return Results.Json(new
        {
            query = q.Trim(),
            focusProjectId,
            focusMeetingId,
            results = hits.Select(ToDto),
            requestId,
        });
    }

    private static IResult EvidenceGet(string? q, string? question, string? projectId, EvidenceService evidence, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var text = q ?? question;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'q' (or 'question') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return EvidencePayload(text, projectId, evidence, requestId);
    }

    private static IResult EvidencePost(EvidenceQueryBody? body, EvidenceService evidence, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var text = body?.Question ?? body?.Q;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'question' (or 'q') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return EvidencePayload(text, body?.ProjectId, evidence, requestId);
    }

    private static IResult EvidencePayload(string question, string? projectId, EvidenceService evidence, string requestId)
    {
        var answer = evidence.Query(question.Trim(), projectId);
        return Results.Json(new
        {
            question = answer.Question,
            answerType = answer.AnswerType,
            answer = answer.Answer,
            value = answer.Value,
            projectId = answer.ProjectId,
            organizationId = answer.OrganizationId,
            citations = answer.Citations.Select(c => new
            {
                c.Kind,
                c.EntityType,
                c.EntityId,
                c.Label,
                c.Path,
                c.Snippet,
                c.ProjectId,
            }),
            status = answer.Status,
            requestId,
        });
    }

    private static object ToDto(GlobalSearchHit hit) => new
    {
        entityType = hit.EntityType,
        entityId = hit.EntityId,
        title = hit.Title,
        snippet = hit.Snippet,
        score = hit.Score,
        projectId = hit.ProjectId,
        path = hit.Path,
        previewKind = hit.PreviewKind,
    };

    public sealed class EvidenceQueryBody
    {
        public string? Question { get; set; }

        public string? Q { get; set; }

        public string? ProjectId { get; set; }
    }
}
