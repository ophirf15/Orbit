using Orbit.Core.Data;

namespace Orbit.Core.Pulse;

/// <summary>
/// Pure attention-reason labels for Needs You Now chips (no UI dependency).
/// Priority: blocked → aged waiting → waiting → email → recently changed → missing next → status.
/// Note: Orbit <c>waiting</c> means the work is waiting (usually on others), not "waiting on you".
/// "Waiting on you" is reserved for actionable active/new concerns on the Needs You Now strip.
/// </summary>
public static class AttentionReasonClassifier
{
    public const int WaitingSeveralDaysHours = 48;

    public const int RecentlyChangedHours = 24;

    public static string Classify(
        string? status,
        string? nextAction,
        string? sourceKind,
        string? updatedAt,
        DateTimeOffset? now = null,
        int? ageHoursOverride = null,
        string? waitingFollowUpAt = null,
        string? waitingSatisfiedAt = null)
    {
        var normalized = NormalizeStatus(status);
        var clock = now ?? DateTimeOffset.UtcNow;
        var ageHours = ageHoursOverride ?? TryAgeHours(updatedAt, clock);

        if (normalized == TaskStatuses.Blocked)
        {
            return "Blocked";
        }

        if (normalized == TaskStatuses.Waiting)
        {
            if (!string.IsNullOrWhiteSpace(waitingSatisfiedAt))
            {
                return "Waiting";
            }

            if (WaitingOnStaleRanker.IsFollowUpOverdue(waitingFollowUpAt, clock))
            {
                return "Follow-up due";
            }

            if (ageHours is >= WaitingSeveralDaysHours)
            {
                return "Waiting several days";
            }

            return "Waiting";
        }

        if (IsEmailSource(sourceKind))
        {
            return "New email";
        }

        if (ageHours is >= 0 and < RecentlyChangedHours)
        {
            return "Recently changed";
        }

        if (string.IsNullOrWhiteSpace(nextAction))
        {
            return "Needs next move";
        }

        return normalized switch
        {
            TaskStatuses.Active => "Waiting on you",
            TaskStatuses.NotStarted => "Waiting on you",
            TaskStatuses.Complete => "Complete",
            TaskStatuses.Archived => "Archived",
            _ => string.IsNullOrWhiteSpace(status) ? "Needs attention" : status.Trim(),
        };
    }

    public static int? TryAgeHours(string? updatedAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(updatedAt)
            || !DateTimeOffset.TryParse(updatedAt, out var when))
        {
            return null;
        }

        var hours = (int)Math.Floor((now - when).TotalHours);
        return hours < 0 ? 0 : hours;
    }

    private static bool IsEmailSource(string? sourceKind) =>
        string.Equals(sourceKind, "email", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        var s = status.Trim().ToLowerInvariant();
        return s switch
        {
            TaskStatuses.Blocked => TaskStatuses.Blocked,
            TaskStatuses.Waiting => TaskStatuses.Waiting,
            TaskStatuses.Active => TaskStatuses.Active,
            TaskStatuses.NotStarted => TaskStatuses.NotStarted,
            TaskStatuses.Complete => TaskStatuses.Complete,
            TaskStatuses.Archived => TaskStatuses.Archived,
            _ => s,
        };
    }
}
