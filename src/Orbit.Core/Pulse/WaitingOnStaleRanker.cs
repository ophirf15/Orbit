namespace Orbit.Core.Pulse;

/// <summary>
/// Pure helpers for waiting-on depth: stale detection and ranking.
/// Stale = open wait past follow-up date, or aged beyond the several-days threshold.
/// </summary>
public static class WaitingOnStaleRanker
{
    public const int DefaultFollowUpDays = 3;

    public sealed record WaitingSignal(
        string TaskId,
        string? WaitingOnLabel,
        string? FollowUpAt,
        string? Cadence,
        string? SatisfiedAt,
        string UpdatedAt,
        string Status,
        int AgeHours);

    public sealed record RankedWaiting(
        WaitingSignal Signal,
        bool IsStale,
        bool FollowUpOverdue,
        int StaleScore);

    /// <summary>True when an open wait should surface as stale attention.</summary>
    public static bool IsStale(
        string? status,
        string? followUpAt,
        string? satisfiedAt,
        string? updatedAt,
        DateTimeOffset? now = null,
        int? ageHoursOverride = null)
    {
        if (!IsOpenWaiting(status, satisfiedAt))
        {
            return false;
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        if (IsFollowUpOverdue(followUpAt, clock))
        {
            return true;
        }

        var age = ageHoursOverride ?? AttentionReasonClassifier.TryAgeHours(updatedAt, clock);
        return age is >= AttentionReasonClassifier.WaitingSeveralDaysHours;
    }

    public static bool IsFollowUpOverdue(string? followUpAt, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(followUpAt)
            || !DateTimeOffset.TryParse(followUpAt, out var when))
        {
            return false;
        }

        var clock = now ?? DateTimeOffset.UtcNow;
        // Date-only follow-ups are overdue at the start of that UTC day.
        return when.ToUniversalTime().Date <= clock.ToUniversalTime().Date;
    }

    public static bool IsOpenWaiting(string? status, string? satisfiedAt)
    {
        if (!string.IsNullOrWhiteSpace(satisfiedAt))
        {
            return false;
        }

        return string.Equals(status, "waiting", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "blocked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Higher score = more urgent. Past follow-up outranks age-only stale.
    /// </summary>
    public static int ComputeStaleScore(
        string? followUpAt,
        int ageHours,
        DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var overdue = IsFollowUpOverdue(followUpAt, clock);
        var ageStale = ageHours >= AttentionReasonClassifier.WaitingSeveralDaysHours;
        if (!overdue && !ageStale)
        {
            return 0;
        }

        var score = 0;
        if (overdue)
        {
            score += 1_000;
            if (DateTimeOffset.TryParse(followUpAt, out var when))
            {
                var daysLate = Math.Max(0, (int)(clock.ToUniversalTime().Date - when.ToUniversalTime().Date).TotalDays);
                score += Math.Min(daysLate, 365);
            }
        }

        if (ageStale)
        {
            score += 100 + Math.Min(ageHours, 24 * 90);
        }

        return score;
    }

    public static IReadOnlyList<RankedWaiting> Rank(
        IEnumerable<WaitingSignal> signals,
        DateTimeOffset? now = null,
        int take = 8)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        var limit = Math.Clamp(take, 1, 50);
        return signals
            .Where(s => IsOpenWaiting(s.Status, s.SatisfiedAt))
            .Select(s =>
            {
                var overdue = IsFollowUpOverdue(s.FollowUpAt, clock);
                var score = ComputeStaleScore(s.FollowUpAt, s.AgeHours, clock);
                return new RankedWaiting(
                    s,
                    IsStale: score > 0,
                    FollowUpOverdue: overdue,
                    StaleScore: score);
            })
            .OrderByDescending(r => r.StaleScore)
            .ThenByDescending(r => r.Signal.AgeHours)
            .Take(limit)
            .ToList();
    }

    /// <summary>Default follow-up ISO date (UTC date) when capture seeds a wait.</summary>
    public static string DefaultFollowUpAt(DateTimeOffset? now = null, int days = DefaultFollowUpDays)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        return clock.ToUniversalTime().Date.AddDays(Math.Max(1, days)).ToString("yyyy-MM-dd");
    }
}
