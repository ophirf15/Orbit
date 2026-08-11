using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Calendar;

namespace Orbit.Core.Host.Api;

public sealed class CalendarSubscribeRequest
{
    public string? Path { get; set; }

    public string? Url { get; set; }

    public string? DisplayName { get; set; }
}

public sealed class CalendarSourceEnabledRequest
{
    public bool? Enabled { get; set; }
}

public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Calendar, GetContext);
        app.MapPost(HostEndpoints.CalendarSync, Sync);
        app.MapGet(HostEndpoints.CalendarSources, ListSources);
        app.MapPatch(HostEndpoints.CalendarSourceById, SetSourceEnabled);
        app.MapPost(HostEndpoints.CalendarSubscribe, Subscribe);
        return app;
    }

    private static IResult GetContext(
        CalendarReadStore store,
        HttpContext http,
        int? days = null,
        int? limit = null,
        DateTimeOffset? changedSince = null)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var windowDays = days is > 0 and <= 90 ? days.Value : 14;
        var take = limit is > 0 and <= 100 ? limit.Value : 40;
        var meetings = store.GetUpcomingContext(TimeSpan.FromDays(windowDays), take, changedSince);
        return Results.Json(new
        {
            windowDays,
            changedSince,
            meetings = meetings.Select(ToMeetingDto),
            requestId,
        });
    }

    private static IResult Sync(CalendarSyncService sync, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var result = sync.Sync();
            hub.Publish(new OrbitEvent
            {
                Type = "calendar.synced",
                Payload = new
                {
                    sourcesUpserted = result.SourcesUpserted,
                    eventsUpserted = result.EventsUpserted,
                    linksCreated = result.LinksCreated,
                },
            });

            return Results.Json(new
            {
                sourcesUpserted = result.SourcesUpserted,
                eventsUpserted = result.EventsUpserted,
                linksCreated = result.LinksCreated,
                attentionUpdated = result.AttentionUpdated,
                providerStatuses = result.ProviderStatuses,
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

    private static IResult ListSources(CalendarReadStore store, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var sources = store.ListSources();
        return Results.Json(new
        {
            sources = sources.Select(s => new
            {
                id = s.Id,
                name = s.Name,
                provider = s.Provider,
                mailboxName = s.MailboxName,
                calendarName = s.CalendarName,
                accountHint = s.AccountHint,
                configUri = s.ConfigUri,
                enabled = s.Enabled,
                lastSyncAt = s.LastSyncAt,
                lastSyncStatus = s.LastSyncStatus,
                lastSyncError = s.LastSyncError,
            }),
            requestId,
        });
    }

    private static IResult SetSourceEnabled(
        string id,
        CalendarSourceEnabledRequest? body,
        CalendarReadStore store,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body?.Enabled is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'enabled' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var source = store.SetEnabled(id, body.Enabled.Value);
            return Results.Json(new
            {
                id = source.Id,
                name = source.Name,
                enabled = source.Enabled,
                requestId,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static IResult Subscribe(CalendarSubscribeRequest? body, CalendarSyncService sync, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var uri = !string.IsNullOrWhiteSpace(body?.Path)
            ? body!.Path!.Trim()
            : body?.Url?.Trim();
        if (string.IsNullOrWhiteSpace(uri))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide path or url for an ICS subscription.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var id = sync.SubscribeIcs(uri, body?.DisplayName);
            return Results.Json(new { sourceId = id, path = uri, requestId }, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static object ToMeetingDto(CalendarContextMeeting m) => new
    {
        id = m.Id,
        title = m.Title,
        startsAt = m.StartsAt,
        endsAt = m.EndsAt,
        location = m.Location,
        attentionScore = m.AttentionScore,
        sourceId = m.SourceId,
        sourceName = m.SourceName,
        mailboxName = m.MailboxName,
        calendarName = m.CalendarName,
        organizer = m.Organizer,
        updatedAt = m.UpdatedAt,
        linkedEntities = m.LinkedEntities.Select(l => new
        {
            entityType = l.EntityType,
            entityId = l.EntityId,
            label = l.Label,
            confidence = l.Confidence,
        }),
    };
}
