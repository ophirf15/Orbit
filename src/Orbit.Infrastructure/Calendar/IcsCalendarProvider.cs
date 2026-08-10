using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Calendar;

/// <summary>Read-only ICS file path or HTTP(S) URL calendar provider.</summary>
public sealed class IcsCalendarProvider : ICalendarProvider
{
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private readonly string _uriOrPath;
    private readonly string? _displayName;
    private readonly HttpClient? _http;

    public IcsCalendarProvider(string uriOrPath, string? displayName = null, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uriOrPath);
        _uriOrPath = uriOrPath.Trim();
        _displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        _http = http;
    }

    public string ProviderId => CalendarProviders.Ics;

    public async Task<CalendarProviderResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await LoadTextAsync(cancellationToken).ConfigureAwait(false);
            var events = IcsVEventParser.Parse(text);
            var name = _displayName
                ?? (IsHttp(_uriOrPath) ? "ICS feed" : Path.GetFileName(_uriOrPath));
            var source = new CalendarSourceSnapshot
            {
                ExternalKey = NormalizeKey(_uriOrPath),
                Name = name,
                MailboxName = null,
                CalendarName = name,
                AccountHint = IsHttp(_uriOrPath) ? "url" : "file",
                ConfigUri = _uriOrPath,
                Events = events,
            };

            return new CalendarProviderResult
            {
                Available = true,
                StatusMessage = $"Loaded {events.Count} event(s) from ICS.",
                Sources = [source],
            };
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException or UnauthorizedAccessException)
        {
            return new CalendarProviderResult
            {
                Available = false,
                StatusMessage = "ICS read failed: " + ex.Message,
                Sources = [],
            };
        }
    }

    private async Task<string> LoadTextAsync(CancellationToken cancellationToken)
    {
        if (IsHttp(_uriOrPath))
        {
            var client = _http ?? SharedHttp;
            using var response = await client.GetAsync(_uriOrPath, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(_uriOrPath))
        {
            throw new FileNotFoundException("ICS file not found.", _uriOrPath);
        }

        return await File.ReadAllTextAsync(_uriOrPath, cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsHttp(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeKey(string uriOrPath)
    {
        if (IsHttp(uriOrPath))
        {
            return uriOrPath.Trim().ToLowerInvariant();
        }

        return Path.GetFullPath(uriOrPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

internal static class IcsVEventParser
{
    public static IReadOnlyList<CalendarEventSnapshot> Parse(string icsText)
    {
        var unfolded = Unfold(icsText);
        var events = new List<CalendarEventSnapshot>();
        var lines = unfolded.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string>? current = null;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                {
                    var snapshot = ToSnapshot(current);
                    if (snapshot is not null)
                    {
                        events.Add(snapshot);
                    }
                }

                current = null;
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var keyPart = line[..colon];
            var value = line[(colon + 1)..];
            var semi = keyPart.IndexOf(';');
            var name = semi >= 0 ? keyPart[..semi] : keyPart;
            current[name] = Unescape(value);
        }

        return events;
    }

    private static CalendarEventSnapshot? ToSnapshot(Dictionary<string, string> props)
    {
        props.TryGetValue("SUMMARY", out var summary);
        props.TryGetValue("UID", out var uid);
        props.TryGetValue("DESCRIPTION", out var body);
        props.TryGetValue("LOCATION", out var location);
        props.TryGetValue("ORGANIZER", out var organizer);
        props.TryGetValue("DTSTART", out var dtStart);
        props.TryGetValue("DTEND", out var dtEnd);

        var title = string.IsNullOrWhiteSpace(summary) ? "(untitled)" : summary.Trim();
        var starts = ParseDate(dtStart);
        var ends = ParseDate(dtEnd);
        var externalUid = string.IsNullOrWhiteSpace(uid)
            ? $"generated:{title}:{starts?.ToString("O") ?? "none"}"
            : uid.Trim();

        if (!string.IsNullOrWhiteSpace(organizer) && organizer.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            organizer = organizer["mailto:".Length..];
        }

        return new CalendarEventSnapshot
        {
            ExternalUid = externalUid,
            Title = title,
            StartsAt = starts,
            EndsAt = ends,
            Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
            Body = string.IsNullOrWhiteSpace(body) ? null : body.Trim(),
            Organizer = string.IsNullOrWhiteSpace(organizer) ? null : organizer.Trim(),
        };
    }

    private static string Unfold(string text)
    {
        // RFC 5545: lines folded with CRLF + space/tab
        return Regex.Replace(text, @"\r?\n[ \t]", string.Empty);
    }

    private static string Unescape(string value) =>
        value
            .Replace(@"\n", "\n", StringComparison.Ordinal)
            .Replace(@"\N", "\n", StringComparison.Ordinal)
            .Replace(@"\,", ",", StringComparison.Ordinal)
            .Replace(@"\;", ";", StringComparison.Ordinal)
            .Replace(@"\\", "\\", StringComparison.Ordinal);

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        // Strip VALUE=DATE: prefix already handled by key split; value may be bare.
        if (value.Length == 8 && DateTime.TryParseExact(
                value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dateOnly))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc));
        }

        if (value.EndsWith('Z')
            && DateTime.TryParseExact(
                value,
                ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var utc))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }

        if (DateTime.TryParseExact(
                value,
                ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmmsszzz"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var local))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.ToUniversalTime();
        }

        return null;
    }
}
