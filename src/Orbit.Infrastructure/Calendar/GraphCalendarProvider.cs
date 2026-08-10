namespace Orbit.Infrastructure.Calendar;

/// <summary>
/// Optional Microsoft Graph calendar provider stub. Not wired for OAuth —
/// returns unavailable so domain storage stays provider-agnostic.
/// </summary>
public sealed class GraphCalendarProvider : ICalendarProvider
{
    public string ProviderId => CalendarProviders.Graph;

    public Task<CalendarProviderResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CalendarProviderResult
        {
            Available = false,
            StatusMessage =
                "Microsoft Graph calendar is architected but not configured. " +
                "Use ICS subscriptions or Classic Outlook COM. Graph remains optional (no mandatory Azure).",
            Sources = [],
        });
    }
}
