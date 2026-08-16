using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Capture;

namespace Orbit.Core.Host.Api;

public sealed class CapturePreviewRequest
{
    public string? Text { get; set; }

    public string? DefaultProjectId { get; set; }
}

public static class CapturePreviewEndpoints
{
    public static IEndpointRouteBuilder MapCapturePreviewEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(HostEndpoints.CapturePreview, (
            CapturePreviewRequest? body,
            CapturePreviewAssembler preview,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var result = preview.Assemble(body?.Text, body?.DefaultProjectId);
            return Results.Json(new
            {
                requestId,
                originalText = result.OriginalText,
                title = result.Title,
                brief = result.Brief,
                nextAction = result.NextAction,
                dueAt = result.DueHint,
                waitingOn = result.WaitingOnHint,
                people = result.PeopleHint,
                location = result.LocationHint,
                source = result.Source,
                matchedProject = result.MatchedProject is null
                    ? null
                    : MapProject(result.MatchedProject),
                candidates = result.Candidates.Select(MapProject),
            });
        });

        return app;
    }

    private static object MapProject(CapturePreviewProjectMatch m) => new
    {
        projectId = m.ProjectId,
        name = m.Name,
        score = m.Score,
        reason = m.Reason,
        reasonLabel = m.ReasonLabel,
        autoSelected = m.AutoSelected,
    };
}
