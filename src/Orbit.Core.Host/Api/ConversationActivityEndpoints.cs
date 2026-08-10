using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Data;

namespace Orbit.Core.Host.Api;

/// <summary>
/// Conversation sync (Hermes session mirror) and remote Telegram activity feed.
/// </summary>
public static class ConversationActivityEndpoints
{
    public static IEndpointRouteBuilder MapConversationActivityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(HostEndpoints.ConversationsSync, SyncConversation);
        app.MapGet(HostEndpoints.ActivityRemote, GetRemoteActivity);
        return app;
    }

    private static IResult SyncConversation(
        ConversationSyncBody? body,
        ConversationStore conversations,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Channel))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'channel' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var record = conversations.SyncConversation(
                body.Channel,
                body.HermesSessionId,
                body.HermesSessionKey,
                body.Title,
                body.ExternalThreadId,
                body.ConversationId ?? body.Id);

            return Results.Json(new
            {
                requestId,
                conversation = new
                {
                    record.Id,
                    record.Channel,
                    record.Title,
                    record.ExternalThreadId,
                    hermesSessionId = record.HermesSessionId,
                    hermesSessionKey = record.HermesSessionKey,
                    record.CreatedAt,
                    record.UpdatedAt,
                },
            });
        }
        catch (ArgumentException ex)
        {
            var notFound = ex.ParamName is "conversationId";
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
        }
    }

    private static IResult GetRemoteActivity(
        int? conversationLimit,
        int? auditLimit,
        RemoteActivityStore activity,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var snapshot = activity.GetRemoteActivity(
            conversationLimit ?? 20,
            auditLimit ?? 40);

        return Results.Json(new
        {
            requestId,
            channel = ConversationStore.ChannelTelegram,
            conversations = snapshot.Conversations.Select(c => new
            {
                c.Id,
                c.Channel,
                c.Title,
                hermesSessionId = c.HermesSessionId,
                c.ExternalThreadId,
                c.CreatedAt,
                c.UpdatedAt,
            }),
            auditEvents = snapshot.AuditEvents.Select(a => new
            {
                a.Id,
                a.EventType,
                a.EntityType,
                a.EntityId,
                a.Actor,
                a.Channel,
                hermesSessionId = a.HermesSessionId,
                a.ExternalUserId,
                a.Summary,
                a.CreatedAt,
                detailJson = a.DetailJson,
            }),
        });
    }

    private sealed class ConversationSyncBody
    {
        public string? Channel { get; set; }

        public string? HermesSessionId { get; set; }

        public string? HermesSessionKey { get; set; }

        public string? Title { get; set; }

        public string? ExternalThreadId { get; set; }

        public string? ConversationId { get; set; }

        public string? Id { get; set; }
    }
}
