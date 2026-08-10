namespace Orbit.Infrastructure.Calendar;

public static class CalendarProviders
{
    public const string Outlook = "outlook";
    public const string Ics = "ics";
    public const string Graph = "graph";
}

/// <summary>Read-only calendar provider. Implementations must never write to the calendar store.</summary>
public interface ICalendarProvider
{
    string ProviderId { get; }

    Task<CalendarProviderResult> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class CalendarProviderResult
{
    public required bool Available { get; init; }

    public string? StatusMessage { get; init; }

    public IReadOnlyList<CalendarSourceSnapshot> Sources { get; init; } = [];
}

public sealed class CalendarSourceSnapshot
{
    /// <summary>Stable key within the provider (mailbox+calendar, file path, URL).</summary>
    public required string ExternalKey { get; init; }

    public required string Name { get; init; }

    public string? MailboxName { get; init; }

    public string? CalendarName { get; init; }

    public string? AccountHint { get; init; }

    /// <summary>ICS file path or HTTP(S) URL when provider is ICS.</summary>
    public string? ConfigUri { get; init; }

    public IReadOnlyList<CalendarEventSnapshot> Events { get; init; } = [];
}

public sealed class CalendarEventSnapshot
{
    public required string ExternalUid { get; init; }

    public required string Title { get; init; }

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }

    public string? Location { get; init; }

    public string? Body { get; init; }

    public string? Organizer { get; init; }
}
