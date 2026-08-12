namespace Orbit.Core.Workbench;

/// <summary>Compact “since …” labels from ISO timestamps already stored on graph entities.</summary>
public static class OperationalSinceFormatter
{
    public static string? FormatSince(string? createdAt, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(createdAt)
            || !DateTimeOffset.TryParse(createdAt, out var created))
        {
            return null;
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        var age = clock - created.ToUniversalTime();
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 90)
        {
            var mins = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return mins == 1 ? "Since 1 min ago" : $"Since {mins} min ago";
        }

        if (age.TotalHours < 36)
        {
            var hours = Math.Max(1, (int)Math.Round(age.TotalHours));
            return hours == 1 ? "Since 1 hour ago" : $"Since {hours} hours ago";
        }

        var days = Math.Max(1, (int)Math.Round(age.TotalDays));
        return days == 1 ? "Since yesterday" : $"Since {days} days ago";
    }
}
