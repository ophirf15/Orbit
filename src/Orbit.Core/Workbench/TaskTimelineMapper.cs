namespace Orbit.Core.Workbench;

/// <summary>
/// Pure formatter for task History lines — “what happened on this task?”
/// Host/App supply facts; this class only formats, sorts, and dedupes.
/// </summary>
public static class TaskTimelineMapper
{
    public const int DefaultLimit = 80;

    public static IReadOnlyList<TaskTimelineLine> Map(
        IEnumerable<TaskTimelineFact>? facts,
        DateTimeOffset? now = null,
        int limit = DefaultLimit)
    {
        if (facts is null)
        {
            return [];
        }

        var take = Math.Clamp(limit, 1, 200);
        var clock = now ?? DateTimeOffset.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = new List<TaskTimelineLine>();

        foreach (var fact in facts)
        {
            if (fact is null || string.IsNullOrWhiteSpace(fact.Kind) || string.IsNullOrWhiteSpace(fact.At))
            {
                continue;
            }

            DateTimeOffset? atUtc = null;
            if (DateTimeOffset.TryParse(fact.At, out var parsed))
            {
                atUtc = parsed.ToUniversalTime();
            }

            var text = FormatText(fact);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var dedupe = fact.DedupeKey;
            if (string.IsNullOrWhiteSpace(dedupe))
            {
                var bucket = atUtc?.ToString("yyyy-MM-dd HH:mm") ?? fact.At.Trim();
                dedupe = $"{fact.Kind}|{bucket}|{text}";
            }

            if (!seen.Add(dedupe))
            {
                continue;
            }

            lines.Add(new TaskTimelineLine
            {
                Kind = fact.Kind.Trim(),
                At = fact.At.Trim(),
                WhenLabel = FormatWhen(atUtc, clock),
                Text = text,
                AtUtc = atUtc,
            });
        }

        return lines
            .OrderByDescending(l => l.AtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(l => l.Text, StringComparer.Ordinal)
            .Take(take)
            .ToList();
    }

    /// <summary>Newest N lines for Overview “Recent” preview.</summary>
    public static IReadOnlyList<TaskTimelineLine> TakeRecent(
        IReadOnlyList<TaskTimelineLine>? lines,
        int count = 3)
    {
        if (lines is null || lines.Count == 0 || count <= 0)
        {
            return [];
        }

        return lines.Take(Math.Min(count, lines.Count)).ToList();
    }

    public static string FormatText(TaskTimelineFact fact)
    {
        var summary = Truncate(NormalizeWhitespace(fact.Summary), 72);
        var detail = Truncate(NormalizeWhitespace(fact.Detail), 48);

        return fact.Kind switch
        {
            TaskTimelineKinds.Created => "Task created",
            TaskTimelineKinds.Status => FormatStatus(fact.StatusLabel, summary),
            TaskTimelineKinds.BriefUpdate => string.IsNullOrEmpty(summary)
                ? "Brief updated"
                : $"Brief updated · {summary}",
            TaskTimelineKinds.Note => string.IsNullOrEmpty(summary)
                ? "Note added"
                : $"Note · {summary}",
            TaskTimelineKinds.EmailLinked => string.IsNullOrEmpty(summary)
                ? "Email linked"
                : $"Email · {summary}",
            TaskTimelineKinds.FileLinked => string.IsNullOrEmpty(summary)
                ? "File linked"
                : $"File · {summary}",
            TaskTimelineKinds.BlockerSet => string.IsNullOrEmpty(summary)
                ? "Blocker set"
                : $"Blocker set · {summary}",
            TaskTimelineKinds.BlockerCleared => string.IsNullOrEmpty(summary)
                ? "Blocker cleared"
                : $"Blocker cleared · {summary}",
            TaskTimelineKinds.WaitingOnLinked => FormatWaiting(summary, detail),
            TaskTimelineKinds.Change => FormatChange(fact.SourceEvent, summary),
            _ => string.IsNullOrEmpty(summary)
                ? NormalizeWhitespace(fact.Kind)
                : summary,
        };
    }

    public static string FormatWhen(DateTimeOffset? atUtc, DateTimeOffset now)
    {
        if (atUtc is null)
        {
            return string.Empty;
        }

        var age = now.ToUniversalTime() - atUtc.Value;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalMinutes < 90)
        {
            var mins = Math.Max(1, (int)Math.Round(age.TotalMinutes));
            return mins == 1 ? "1m ago" : $"{mins}m ago";
        }

        if (age.TotalHours < 36)
        {
            var hours = Math.Max(1, (int)Math.Round(age.TotalHours));
            return hours == 1 ? "1h ago" : $"{hours}h ago";
        }

        var days = Math.Max(1, (int)Math.Floor(age.TotalDays));
        if (days < 14)
        {
            return days == 1 ? "Yesterday" : $"{days}d ago";
        }

        return atUtc.Value.ToLocalTime().ToString("MMM d");
    }

    private static string FormatStatus(string? statusLabel, string summary)
    {
        var label = string.IsNullOrWhiteSpace(statusLabel)
            ? (string.IsNullOrEmpty(summary) ? "updated" : summary)
            : statusLabel.Trim();
        return $"Status → {label}";
    }

    private static string FormatWaiting(string summary, string detail)
    {
        if (string.IsNullOrEmpty(summary))
        {
            return "Waiting-on link added";
        }

        return string.IsNullOrEmpty(detail)
            ? $"Waiting on · {summary}"
            : $"Waiting on · {summary} ({detail})";
    }

    private static string FormatChange(string? sourceEvent, string summary)
    {
        var evt = NormalizeWhitespace(sourceEvent);
        if (string.Equals(evt, "operator.briefing", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(summary) ? "Hermes briefing" : $"Hermes briefing · {summary}";
        }

        if (string.Equals(evt, "task.updated", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrEmpty(summary) ? "Task updated" : $"Updated · {summary}";
        }

        if (!string.IsNullOrEmpty(evt))
        {
            var shortEvt = evt.Contains('.') ? evt[(evt.LastIndexOf('.') + 1)..] : evt;
            return string.IsNullOrEmpty(summary)
                ? char.ToUpperInvariant(shortEvt[0]) + shortEvt[1..]
                : $"{shortEvt} · {summary}";
        }

        return string.IsNullOrEmpty(summary) ? "Change recorded" : summary;
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        if (max <= 1)
        {
            return "…";
        }

        return text[..(max - 1)].TrimEnd() + "…";
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
