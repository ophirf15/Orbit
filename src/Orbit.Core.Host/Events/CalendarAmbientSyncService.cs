using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Calendar;

namespace Orbit.Core.Host.Events;

/// <summary>
/// Periodically syncs calendar sources so Core/Pulse have fresh meeting data without requiring
/// a Settings click. Data-only (ADR 0028): the published <c>calendar.synced</c> event no longer
/// triggers a Host agent wake — Hermes cron owns calendar.soon cadence.
/// </summary>
public sealed class CalendarAmbientSyncService : BackgroundService
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly CalendarSyncService _sync;
    private readonly EventHub _hub;
    private readonly ILogger<CalendarAmbientSyncService> _logger;

    public CalendarAmbientSyncService(
        CalendarSyncService sync,
        EventHub hub,
        ILogger<CalendarAmbientSyncService> logger)
    {
        _sync = sync;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = _sync.Sync();
                _hub.Publish(new OrbitEvent
                {
                    Type = "calendar.synced",
                    Payload = new
                    {
                        sourcesUpserted = result.SourcesUpserted,
                        eventsUpserted = result.EventsUpserted,
                        linksCreated = result.LinksCreated,
                        source = "CalendarAmbientSyncService",
                    },
                });
                _logger.LogInformation(
                    "Ambient calendar sync: sources={Sources} events={Events}",
                    result.SourcesUpserted,
                    result.EventsUpserted);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ambient calendar sync skipped or failed.");
            }

            try
            {
                await Task.Delay(DefaultInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
