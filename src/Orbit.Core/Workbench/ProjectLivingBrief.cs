using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Orbit.Core.Data;

namespace Orbit.Core.Workbench;

/// <summary>How Host applies a synthesized living brief.</summary>
public enum LivingBriefApplyMode
{
    /// <summary>
    /// Auto path when summary is blank and/or dossier empty: fill blank summary and empty dossier slots only.
    /// Never overwrites non-empty operator summary.
    /// </summary>
    Baseline,

    /// <summary>
    /// Explicit refresh: fill empty dossier slots; if summary is blank write full brief;
    /// if summary is non-empty, append or replace a dated Auto brief section (operator prose preserved).
    /// </summary>
    Refresh,
}

public sealed record LivingBriefTaskItem(
    string Title,
    string Status,
    string? NextAction,
    string? DueAt,
    string? WaitingOnLabel = null);

public sealed record LivingBriefBlockerItem(string Summary, string Status);

public sealed record LivingBriefNoteItem(string Text, string? CreatedAt = null);

public sealed record LivingBriefContactItem(
    string DisplayName,
    string? Title = null,
    string? OrganizationName = null,
    string? PersonId = null);

public sealed record LivingBriefMeetingItem(string Title, string? StartsAt = null);

/// <summary>Project graph snapshot for living-brief synthesis (pure; no I/O).</summary>
public sealed class ProjectLivingBriefSnapshot
{
    public required string ProjectName { get; init; }

    public string? CurrentSummary { get; init; }

    public bool DossierEmpty { get; init; } = true;

    public IReadOnlyList<string> ExistingPriorities { get; init; } = [];

    public bool HasCriticalContacts { get; init; }

    public IReadOnlyList<LivingBriefTaskItem> OpenTasks { get; init; } = [];

    public IReadOnlyList<LivingBriefBlockerItem> OpenBlockers { get; init; } = [];

    public IReadOnlyList<LivingBriefNoteItem> RecentNotes { get; init; } = [];

    public IReadOnlyList<LivingBriefContactItem> Contacts { get; init; } = [];

    public IReadOnlyList<LivingBriefMeetingItem> UpcomingMeetings { get; init; } = [];
}

/// <summary>Proposed living brief content from graph signals.</summary>
public sealed class ProjectLivingBriefProposal
{
    public string? SummaryText { get; init; }

    public IReadOnlyList<string> CurrentPriorities { get; init; } = [];

    public IReadOnlyList<LivingBriefContactItem> CriticalContacts { get; init; } = [];

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(SummaryText)
        || CurrentPriorities.Count > 0
        || CriticalContacts.Count > 0;
}

/// <summary>Non-destructive merge outcome for Host persistence.</summary>
public sealed class ProjectLivingBriefMergeResult
{
    /// <summary>When true, write <see cref="Summary"/> (may be null to clear — synthesizer never clears).</summary>
    public bool WriteSummary { get; init; }

    public string? Summary { get; init; }

    public bool WritePriorities { get; init; }

    public IReadOnlyList<string> Priorities { get; init; } = [];

    public bool WriteContacts { get; init; }

    public IReadOnlyList<LivingBriefContactItem> Contacts { get; init; } = [];

    public bool Changed => WriteSummary || WritePriorities || WriteContacts;
}

/// <summary>
/// Pure synthesizer: open tasks, blockers, waiting, notes, people, dates → living brief text + dossier enrichments.
/// </summary>
public static partial class ProjectLivingBriefSynthesizer
{
    public const int MaxPriorities = 8;
    public const int MaxContacts = 8;
    public const int MaxLinesPerSection = 5;
    public const int MaxNoteChars = 120;

    public const string AutoBriefMarker = "Auto brief (";

    /// <summary>Build a proposed brief from a project context snapshot.</summary>
    public static ProjectLivingBriefProposal Synthesize(ProjectLivingBriefSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var openBlockers = snapshot.OpenBlockers
            .Where(b => !string.Equals(b.Status, "cleared", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(b.Status, "resolved", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(b.Summary))
            .Take(MaxLinesPerSection)
            .ToList();

        var waiting = snapshot.OpenTasks
            .Where(t => string.Equals(t.Status, TaskStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
                        || !string.IsNullOrWhiteSpace(t.WaitingOnLabel))
            .Take(MaxLinesPerSection)
            .ToList();

        var commitments = snapshot.OpenTasks
            .Where(t => !string.Equals(t.Status, TaskStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(t.Status, TaskStatuses.Blocked, StringComparison.OrdinalIgnoreCase))
            .Select(CommitmentLine)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxPriorities)
            .ToList();

        var risks = openBlockers
            .Select(b => b.Summary.Trim())
            .Concat(snapshot.OpenTasks
                .Where(t => string.Equals(t.Status, TaskStatuses.Blocked, StringComparison.OrdinalIgnoreCase))
                .Select(t => string.IsNullOrWhiteSpace(t.NextAction) ? t.Title.Trim() : $"{t.Title.Trim()} — {t.NextAction!.Trim()}"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxLinesPerSection)
            .ToList();

        var people = snapshot.Contacts
            .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
            .Take(MaxContacts)
            .ToList();

        var upcoming = snapshot.UpcomingMeetings
            .Where(m => !string.IsNullOrWhiteSpace(m.Title))
            .Concat(snapshot.OpenTasks
                .Where(t => !string.IsNullOrWhiteSpace(t.DueAt))
                .Select(t => new LivingBriefMeetingItem($"Due · {t.Title.Trim()}", t.DueAt)))
            .Take(MaxLinesPerSection)
            .ToList();

        var notes = snapshot.RecentNotes
            .Where(n => !string.IsNullOrWhiteSpace(n.Text))
            .Take(3)
            .ToList();

        var objective = ResolveObjective(snapshot.OpenTasks, commitments);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(objective))
        {
            sb.Append("Objective: ").Append(objective.Trim());
        }

        AppendBullets(sb, "Commitments", commitments);
        AppendBullets(sb, "Risks / blockers", risks);
        AppendBullets(
            sb,
            "Waiting",
            waiting.Select(WaitingLine).Where(s => !string.IsNullOrWhiteSpace(s))!);
        AppendBullets(
            sb,
            "People",
            people.Select(PersonLine).Where(s => !string.IsNullOrWhiteSpace(s))!);
        AppendBullets(
            sb,
            "Upcoming",
            upcoming.Select(UpcomingLine).Where(s => !string.IsNullOrWhiteSpace(s))!);
        AppendBullets(
            sb,
            "Recent",
            notes.Select(n => Truncate(CollapseWhitespace(n.Text), MaxNoteChars)));

        var summary = sb.Length == 0 ? null : sb.ToString().Trim();
        if (summary is null
            && !string.IsNullOrWhiteSpace(snapshot.ProjectName)
            && (snapshot.OpenTasks.Count > 0 || openBlockers.Count > 0 || notes.Count > 0))
        {
            summary = $"Objective: Keep {snapshot.ProjectName.Trim()} moving.";
        }

        return new ProjectLivingBriefProposal
        {
            SummaryText = summary,
            CurrentPriorities = commitments.Take(MaxPriorities).ToList(),
            CriticalContacts = people,
        };
    }

    /// <summary>
    /// Merge proposal into existing operator state without silently wiping prose.
    /// </summary>
    public static ProjectLivingBriefMergeResult Merge(
        ProjectLivingBriefSnapshot snapshot,
        ProjectLivingBriefProposal proposal,
        LivingBriefApplyMode mode,
        DateTimeOffset? clock = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(proposal);

        if (!proposal.HasContent)
        {
            return new ProjectLivingBriefMergeResult();
        }

        var now = clock ?? DateTimeOffset.UtcNow;
        var summaryEmpty = string.IsNullOrWhiteSpace(snapshot.CurrentSummary);
        var writeSummary = false;
        string? summary = null;

        switch (mode)
        {
            case LivingBriefApplyMode.Baseline:
                if (summaryEmpty && !string.IsNullOrWhiteSpace(proposal.SummaryText))
                {
                    writeSummary = true;
                    summary = proposal.SummaryText!.Trim();
                }

                break;

            case LivingBriefApplyMode.Refresh:
                if (string.IsNullOrWhiteSpace(proposal.SummaryText))
                {
                    break;
                }

                writeSummary = true;
                summary = summaryEmpty
                    ? proposal.SummaryText!.Trim()
                    : MergeAutoBriefSection(snapshot.CurrentSummary!.Trim(), proposal.SummaryText!.Trim(), now);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var writePriorities = false;
        IReadOnlyList<string> priorities = [];
        if (snapshot.ExistingPriorities.Count == 0 && proposal.CurrentPriorities.Count > 0)
        {
            writePriorities = true;
            priorities = proposal.CurrentPriorities.Take(MaxPriorities).ToList();
        }
        else if (mode == LivingBriefApplyMode.Refresh
                 && proposal.CurrentPriorities.Count > 0)
        {
            var merged = snapshot.ExistingPriorities
                .Concat(proposal.CurrentPriorities)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxPriorities)
                .ToList();
            if (!SameStringList(merged, snapshot.ExistingPriorities))
            {
                writePriorities = true;
                priorities = merged;
            }
        }

        var writeContacts = false;
        IReadOnlyList<LivingBriefContactItem> contacts = [];
        if (!snapshot.HasCriticalContacts && proposal.CriticalContacts.Count > 0)
        {
            writeContacts = true;
            contacts = proposal.CriticalContacts.Take(MaxContacts).ToList();
        }

        return new ProjectLivingBriefMergeResult
        {
            WriteSummary = writeSummary,
            Summary = summary,
            WritePriorities = writePriorities,
            Priorities = priorities,
            WriteContacts = writeContacts,
            Contacts = contacts,
        };
    }

    /// <summary>
    /// Replace an existing trailing Auto brief section, or append a new dated section.
    /// Operator prose before the marker is always preserved.
    /// </summary>
    public static string MergeAutoBriefSection(string existingSummary, string proposedBody, DateTimeOffset clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedBody);
        var date = clock.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var section = $"---\n{AutoBriefMarker}{date}):\n{proposedBody.Trim()}";

        if (string.IsNullOrWhiteSpace(existingSummary))
        {
            return proposedBody.Trim();
        }

        var existing = existingSummary.TrimEnd();
        var idx = existing.IndexOf("---\n" + AutoBriefMarker, StringComparison.Ordinal);
        if (idx < 0)
        {
            idx = existing.IndexOf(AutoBriefMarker, StringComparison.Ordinal);
            if (idx > 0)
            {
                // Prefer cutting at a preceding horizontal rule when present.
                var rule = existing.LastIndexOf("\n---\n", idx, StringComparison.Ordinal);
                if (rule >= 0)
                {
                    idx = rule + 1;
                }
            }
        }

        if (idx >= 0)
        {
            var head = existing[..idx].TrimEnd();
            return string.IsNullOrWhiteSpace(head) ? section : head + "\n\n" + section;
        }

        return existing + "\n\n" + section;
    }

    public static bool NeedsBaseline(string? summary, bool dossierEmpty) =>
        string.IsNullOrWhiteSpace(summary) || dossierEmpty;

    private static string? ResolveObjective(
        IReadOnlyList<LivingBriefTaskItem> tasks,
        IReadOnlyList<string> commitments)
    {
        var withNext = tasks.FirstOrDefault(t =>
            string.Equals(t.Status, TaskStatuses.Active, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(t.NextAction));
        if (withNext is not null)
        {
            return withNext.NextAction!.Trim();
        }

        if (commitments.Count > 0)
        {
            return commitments[0];
        }

        var any = tasks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Title));
        return any?.Title.Trim();
    }

    private static string CommitmentLine(LivingBriefTaskItem t)
    {
        var title = t.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(t.NextAction)
            ? title
            : $"{title} — {t.NextAction.Trim()}";
    }

    private static string WaitingLine(LivingBriefTaskItem t)
    {
        var who = string.IsNullOrWhiteSpace(t.WaitingOnLabel) ? null : t.WaitingOnLabel.Trim();
        var title = t.Title?.Trim() ?? string.Empty;
        if (who is null)
        {
            return string.IsNullOrWhiteSpace(t.NextAction) ? title : $"{title} — {t.NextAction.Trim()}";
        }

        return string.IsNullOrWhiteSpace(title) ? who : $"{title} (waiting on {who})";
    }

    private static string PersonLine(LivingBriefContactItem c)
    {
        var name = c.DisplayName.Trim();
        var role = string.IsNullOrWhiteSpace(c.Title) ? c.OrganizationName : c.Title;
        return string.IsNullOrWhiteSpace(role) ? name : $"{name} ({role.Trim()})";
    }

    private static string UpcomingLine(LivingBriefMeetingItem m)
    {
        var title = m.Title.Trim();
        if (string.IsNullOrWhiteSpace(m.StartsAt))
        {
            return title;
        }

        var when = TryFormatDate(m.StartsAt);
        return when is null ? title : $"{title} · {when}";
    }

    private static string? TryFormatDate(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
        {
            return dto.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
        {
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var trimmed = raw.Trim();
        return trimmed.Length >= 10 ? trimmed[..10] : trimmed;
    }

    private static void AppendBullets(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        var list = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(MaxLinesPerSection).ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(heading).Append(':');
        foreach (var line in list)
        {
            sb.Append("\n- ").Append(line.Trim());
        }
    }

    private static string CollapseWhitespace(string text) =>
        WhitespaceRegex().Replace(text.Trim(), " ");

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        return text[..(max - 1)].TrimEnd() + "…";
    }

    private static bool SameStringList(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
