using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Context;

namespace Orbit.Core.Host.Api;

public static class ContextBundleEndpoints
{
    public static IEndpointRouteBuilder MapContextBundleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.ContextBundle, GetBundle);
        return app;
    }

    private static IResult GetBundle(
        string? targetType,
        string? targetId,
        string? attentionProjectId,
        ContextBundleService bundles,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
        {
            return Results.Json(
                ApiErrors.Create(
                    ApiErrorCodes.BadRequest,
                    "Query parameters 'targetType' and 'targetId' are required.",
                    requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var bundle = bundles.GetBundle(targetType, targetId, attentionProjectId);
            if (bundle is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Context target was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(ToDto(bundle, requestId));
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    internal static object ToDto(ContextBundle bundle, string requestId) => new
    {
        targetType = bundle.TargetType,
        targetId = bundle.TargetId,
        projectId = bundle.ProjectId,
        projectName = bundle.ProjectName,
        projectSummary = bundle.ProjectSummary,
        workstreamId = bundle.WorkstreamId,
        taskId = bundle.TaskId,
        attentionProjectId = bundle.AttentionProjectId,
        attentionAligned = bundle.AttentionAligned,
        tasks = bundle.Tasks.Select(t => new
        {
            taskId = t.TaskId,
            title = t.Title,
            status = t.Status,
            nextAction = t.NextAction,
            workstreamId = t.WorkstreamId,
        }),
        blockers = bundle.Blockers.Select(b => new
        {
            id = b.Id,
            summary = b.Summary,
            status = b.Status,
            taskId = b.TaskId,
        }),
        notes = bundle.Notes.Select(n => new
        {
            id = n.Id,
            originalText = n.OriginalText,
            createdAt = n.CreatedAt,
        }),
        emails = bundle.Emails.Select(e => new
        {
            id = e.Id,
            subject = e.Subject,
            sentAt = e.SentAt,
            bodyPreview = e.BodyPreview,
            extractions = e.Extractions.Select(x => new
            {
                id = x.Id,
                extractionType = x.ExtractionType,
                summary = x.Summary,
                projectId = x.ProjectId,
                workstreamId = x.WorkstreamId,
                confidence = x.Confidence,
            }),
        }),
        contacts = bundle.Contacts.Select(c => new
        {
            personId = c.PersonId,
            displayName = c.DisplayName,
        }),
        files = bundle.Files.Select(f => new
        {
            id = f.Id,
            displayName = f.DisplayName,
            path = f.Path,
            extension = f.Extension,
        }),
        meetings = bundle.Meetings.Select(m => new
        {
            id = m.Id,
            title = m.Title,
            startsAt = m.StartsAt,
            endsAt = m.EndsAt,
            location = m.Location,
            attentionScore = m.AttentionScore,
            sourceName = m.SourceName,
            mailboxName = m.MailboxName,
            calendarName = m.CalendarName,
        }),
        suggestions = bundle.Suggestions.Select(s => new
        {
            id = s.Id,
            summary = s.Summary,
            status = s.Status,
            suggestionType = s.SuggestionType,
            noteId = s.NoteId,
            confidence = s.Confidence,
        }),
        relatedEntities = bundle.RelatedEntities.Select(r => new
        {
            entityType = r.EntityType,
            entityId = r.EntityId,
            label = r.Label,
            relationshipType = r.RelationshipType,
        }),
        requestId,
    };
}
