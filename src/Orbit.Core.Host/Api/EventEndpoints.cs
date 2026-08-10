using System.Text;
using Orbit.Core.Host;
using Orbit.Core.Host.Events;

namespace Orbit.Core.Host.Api;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Events, async (HttpContext http, EventHub hub, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";

            await http.Response.Body.FlushAsync(ct);

            // Immediate hello so clients/tests need not wait for heartbeat interval.
            await WriteEventAsync(http, new OrbitEvent
            {
                Type = "connected",
                Payload = new { stream = "orbit" },
            }, ct);

            await foreach (var orbitEvent in hub.SubscribeAsync(ct))
            {
                await WriteEventAsync(http, orbitEvent, ct);
            }
        });

        return app;
    }

    private static async Task WriteEventAsync(HttpContext http, OrbitEvent orbitEvent, CancellationToken ct)
    {
        var payload = EventHub.Serialize(orbitEvent);
        var frame = $"event: orbit\ndata: {payload}\n\n";
        var bytes = Encoding.UTF8.GetBytes(frame);
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
