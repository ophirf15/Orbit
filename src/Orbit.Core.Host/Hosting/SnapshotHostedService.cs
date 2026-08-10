using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Sync;

namespace Orbit.Core.Host.Hosting;

/// <summary>
/// Debounced periodic snapshot publisher. Never throws out of the host loop.
/// </summary>
public sealed class SnapshotHostedService : BackgroundService
{
    private readonly SnapshotService _snapshots;
    private readonly SnapshotSyncOptions _options;
    private readonly ILogger<SnapshotHostedService>? _logger;
    private readonly Func<DateTimeOffset> _utcNow;

    public SnapshotHostedService(
        SnapshotService snapshots,
        SnapshotSyncOptions? options = null,
        ILogger<SnapshotHostedService>? logger = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _snapshots = snapshots;
        _options = options ?? new SnapshotSyncOptions();
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = _options.PollInterval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(5)
            : _options.PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                if (_snapshots.ShouldAutoSnapshot(_utcNow()))
                {
                    _snapshots.CreateSnapshot();
                    _logger?.LogInformation("Automatic Orbit snapshot published.");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Automatic snapshot skipped.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_snapshots.ShouldAutoSnapshot(_utcNow())
                || _snapshots.GetStatus().LocalDirty
                || _snapshots.GetStatus().Kind is SyncStatusKind.LocalAhead or SyncStatusKind.Idle)
            {
                var status = _snapshots.GetStatus();
                if (!string.IsNullOrWhiteSpace(status.SyncFolder)
                    && status.Kind != SyncStatusKind.Conflict
                    && status.Kind != SyncStatusKind.Unavailable)
                {
                    try
                    {
                        _snapshots.NotifyActivity();
                        // Force publish on graceful shutdown when folder is usable.
                        _snapshots.CreateSnapshot();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Shutdown snapshot skipped.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Shutdown snapshot evaluation failed.");
        }

        await base.StopAsync(cancellationToken);
    }
}
