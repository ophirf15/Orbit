namespace Orbit.Core.Host.Events;

/// <summary>
/// Formerly queued Host-owned duty.scan LLM wakes (ADR 0026/0027).
/// ADR 0028: Hermes cron owns morning/evening/ambient cadence. This service stays
/// registered as a no-op so Host wiring does not break; it no longer pokes Hermes and
/// takes no dependency on <see cref="OperatorWakeService"/>.
/// </summary>
public sealed class AmbientPulseService : BackgroundService
{
    private readonly ILogger<AmbientPulseService> _logger;

    public AmbientPulseService(ILogger<AmbientPulseService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AmbientPulseService idle (ADR 0028): Hermes cron owns duty.scan cadence; Host will not queue LLM wakes.");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }
}
