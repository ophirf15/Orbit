namespace Orbit_App.Services;

/// <summary>In-chat Hermes status lines: real tool progress when available, else light rotating copy.</summary>
public static class HermesThinkingCopy
{
    private static readonly string[] IdleLines =
    [
        "Hermes is pondering…",
        "Thinking about it…",
        "Checking the workbench…",
        "Riffling through Orbit…",
        "Consulting the tools…",
        "One moment — gathering context…",
        "Hmm, looking this up…",
    ];

    /// <summary>Workbench duty-banner stages while Hermes organizes pushed mail.</summary>
    private static readonly string[] DutyStageLines =
    [
        "Reading the email…",
        "Matching projects…",
        "Updating the workbench…",
        "Task is on the workbench — Hermes is finishing the notes…",
        "Still writing the living brief and next action…",
        "Polishing the concern — almost there…",
    ];

    public static string NextIdleLine(int tick) =>
        IdleLines[Math.Abs(tick) % IdleLines.Length];

    public static string NextDutyStage(TimeSpan elapsed, int tick)
    {
        // Prefer elapsed stages early so the banner clearly advances, then rotate.
        var byElapsed = elapsed.TotalSeconds switch
        {
            < 5 => 0,
            < 10 => 1,
            < 16 => 2,
            < 35 => 3,
            < 55 => 4,
            _ => 5,
        };

        if (elapsed.TotalSeconds < 55)
        {
            return DutyStageLines[byElapsed];
        }

        // After the floor is usually visible, keep emphasizing notes are still in flight.
        return DutyStageLines[3 + (Math.Abs(tick) % 3)];
    }

    /// <summary>
    /// Once the task is likely on the workbench, prefer note-finishing copy over early stages
    /// (unless Hermes is streaming a concrete tool line).
    /// </summary>
    public static string DutyBannerDetail(TimeSpan elapsed, int tick, string? liveProgress)
    {
        if (!string.IsNullOrWhiteSpace(liveProgress)
            && !LooksLikeGenericStage(liveProgress))
        {
            if (elapsed.TotalSeconds >= 12
                && liveProgress.Contains("Writing the briefing", StringComparison.OrdinalIgnoreCase))
            {
                return "Hermes is finishing the living brief…";
            }

            return liveProgress.Trim();
        }

        return NextDutyStage(elapsed, tick);
    }

    private static bool LooksLikeGenericStage(string text)
    {
        var t = text.Trim();
        return t.StartsWith("Reading the email", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Matching projects", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Updating the workbench", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Queued", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Handed off", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Waking Hermes", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Asking Hermes", StringComparison.OrdinalIgnoreCase)
               || t.StartsWith("Opening a Hermes", StringComparison.OrdinalIgnoreCase);
    }

    public static string FromProgress(string? text, string? toolName, string? status)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            return Orbit.Infrastructure.Hermes.HermesHttpClient.FormatToolProgressLine(toolName, status);
        }

        return NextIdleLine(0);
    }
}
