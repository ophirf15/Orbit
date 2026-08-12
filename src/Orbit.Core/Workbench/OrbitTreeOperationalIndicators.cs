using Orbit.Core.Data;

namespace Orbit.Core.Workbench;

/// <summary>
/// Pure tree/row operational indicators (glyph + caption) — not color-only.
/// </summary>
public static class OrbitTreeOperationalIndicators
{
    // Segoe MDL2 Assets (FontIcon)
    public const string GlyphBlocked = "\uE711";
    public const string GlyphWaiting = "\uE916";
    public const string GlyphActive = "\uE768";
    public const string GlyphNeedsAction = "\uE8FD";
    public const string GlyphCompleted = "\uE73E";
    public const string GlyphProject = "\uE8B7";
    public const string GlyphLimbo = "\uE946";
    public const string GlyphCompletedGroup = "\uE73E";
    public const string GlyphDefault = "\uE8A5";

    public readonly record struct TaskIndicator(string Glyph, string Label, string Tooltip);

    /// <summary>
    /// Map task status (+ optional next-move emptiness) to a subtle glyph/label/tooltip.
    /// </summary>
    public static TaskIndicator ForTaskStatus(string? status, string? nextAction = null)
    {
        var normalized = NormalizeStatus(status);
        var missingNext = string.IsNullOrWhiteSpace(nextAction);

        return normalized switch
        {
            TaskStatuses.Blocked => new TaskIndicator(GlyphBlocked, "Blocked", "Blocked"),
            TaskStatuses.Waiting => new TaskIndicator(GlyphWaiting, "Waiting", "Waiting"),
            TaskStatuses.Complete => new TaskIndicator(GlyphCompleted, "Completed", "Completed"),
            TaskStatuses.Archived => new TaskIndicator(GlyphCompleted, "Completed", "Archived"),
            TaskStatuses.NotStarted => new TaskIndicator(
                GlyphNeedsAction,
                "Needs action",
                missingNext ? "Needs action — no next move yet" : "Needs action"),
            TaskStatuses.Active when missingNext => new TaskIndicator(
                GlyphNeedsAction,
                "Needs action",
                "Needs action — no next move yet"),
            TaskStatuses.Active => new TaskIndicator(GlyphActive, "Active", "Active"),
            _ => new TaskIndicator(
                GlyphNeedsAction,
                string.IsNullOrWhiteSpace(status) ? "Needs action" : status.Trim(),
                string.IsNullOrWhiteSpace(status) ? "Needs action" : status.Trim()),
        };
    }

    /// <summary>
    /// Count open / blocked / waiting from open-task status strings (excludes complete/archived).
    /// </summary>
    public static (int Open, int Blocked, int Waiting) CountOpenTaskStatuses(IEnumerable<string?> statuses)
    {
        var open = 0;
        var blocked = 0;
        var waiting = 0;

        foreach (var status in statuses)
        {
            var normalized = NormalizeStatus(status);
            if (normalized is TaskStatuses.Complete or TaskStatuses.Archived)
            {
                continue;
            }

            open++;
            if (normalized == TaskStatuses.Blocked)
            {
                blocked++;
            }
            else if (normalized == TaskStatuses.Waiting)
            {
                waiting++;
            }
        }

        return (open, blocked, waiting);
    }

    /// <summary>
    /// Project row caption: <c>N open · B blocked · W waiting</c>.
    /// </summary>
    public static string FormatProjectSubtitle(int open, int blocked, int waiting, int done = 0)
    {
        if (open <= 0 && done <= 0)
        {
            return "No open tasks";
        }

        if (open <= 0)
        {
            return done == 1 ? "1 done" : $"{done} done";
        }

        var line = $"{open} open · {blocked} blocked · {waiting} waiting";
        return done > 0 ? $"{line} · {done} done" : line;
    }

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
