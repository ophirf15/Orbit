namespace Orbit_App.ViewModels;

public sealed class WorkbenchVm
{
    public IList<ProjectCellVm> Cells { get; set; } = [];

    public IList<LimboNoteVm> Limbo { get; set; } = [];

    public WorkbenchScopeVm? Scope { get; set; }

    public bool IsProjectScoped =>
        Scope is not null && string.Equals(Scope.Kind, "project", StringComparison.Ordinal);
}

public sealed class WorkbenchScopeVm
{
    public string Kind { get; set; } = "root";

    public string? ProjectId { get; set; }

    public string? ProjectName { get; set; }
}

public sealed class ProjectCellVm
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Summary { get; set; }

    public string Status { get; set; } = string.Empty;

    /// <summary>"project" at root; "task" inside a project board; "limbo" synthetic root cell.</summary>
    public string CellKind { get; set; } = "project";

    public bool IsTaskCell => string.Equals(CellKind, "task", StringComparison.Ordinal);

    public bool IsLimboCell => string.Equals(CellKind, "limbo", StringComparison.Ordinal);

    public IList<CellLineVm> Lines { get; set; } = [];

    public int OpenBlockerCount { get; set; }

    public string? TopBlockerSummary { get; set; }

    public string? UpcomingMeetingTitle { get; set; }

    public string? UpcomingMeetingStartsAt { get; set; }

    public int PendingSuggestionCount { get; set; }

    public string? RecentActivityAt { get; set; }

    /// <summary>Workbench stripe hex (#RRGGBB), or null for theme default.</summary>
    public string? AccentColor { get; set; }

    public int SortOrder { get; set; }

    public double BoardX { get; set; }

    public double BoardY { get; set; }

    public double BoardW { get; set; }

    public double BoardH { get; set; }

    public bool HasSavedLayout { get; set; }

    public bool DossierEmpty { get; set; }

    public bool MissingNextAction { get; set; }

    public string BlockerBadgeText =>
        OpenBlockerCount <= 0 ? string.Empty : OpenBlockerCount == 1 ? "1 blocker" : $"{OpenBlockerCount} blockers";

    public string MeetingText =>
        string.IsNullOrWhiteSpace(UpcomingMeetingTitle) ? string.Empty : $"Meeting · {UpcomingMeetingTitle}";

    public string SuggestionText =>
        PendingSuggestionCount <= 0 ? string.Empty : PendingSuggestionCount == 1 ? "1 suggestion" : $"{PendingSuggestionCount} suggestions";

    public string HygieneText
    {
        get
        {
            var bits = new List<string>();
            if (DossierEmpty)
            {
                bits.Add("empty dossier");
            }

            if (MissingNextAction)
            {
                bits.Add("needs next action");
            }

            return string.Join(" · ", bits);
        }
    }

    public string RecentText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RecentActivityAt))
            {
                return string.Empty;
            }

            return DateTimeOffset.TryParse(RecentActivityAt, out var when)
                ? $"Touched {when.LocalDateTime:g}"
                : $"Touched {RecentActivityAt}";
        }
    }

    public bool HasBlocker => OpenBlockerCount > 0;

    public bool HasMeeting => !string.IsNullOrWhiteSpace(UpcomingMeetingTitle);

    public bool HasSuggestion => PendingSuggestionCount > 0;
}

public sealed class CellLineVm
{
    public string TaskId { get; set; } = string.Empty;

    public string? ProjectId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? NextAction { get; set; }

    public string? Body { get; set; }

    public string? DueAt { get; set; }

    /// <summary>1 = Important, 0 = Less important, null = unset (board treats as less).</summary>
    public int? Priority { get; set; }

    /// <summary>1 = Urgent, 0 = Less urgent, null = auto from due/blocked.</summary>
    public int? Urgency { get; set; }

    public string? SourceKind { get; set; }

    public double? SourceConfidence { get; set; }

    public string? SourceMatchReason { get; set; }

    public string? WaitingOnLabel { get; set; }

    public string? WaitingOnPersonId { get; set; }

    public string? WaitingOnOrganizationId { get; set; }

    public string? WaitingFollowUpAt { get; set; }

    public string? WaitingCadence { get; set; }

    public string? WaitingSatisfiedAt { get; set; }

    public string? WaitingEvidenceRef { get; set; }

    public string? CreatedAt { get; set; }

    public string? UpdatedAt { get; set; }

    public bool HasOpenWaiting =>
        string.IsNullOrWhiteSpace(WaitingSatisfiedAt)
        && (!string.IsNullOrWhiteSpace(WaitingOnLabel)
            || !string.IsNullOrWhiteSpace(WaitingOnPersonId)
            || !string.IsNullOrWhiteSpace(WaitingOnOrganizationId)
            || string.Equals(Status, "waiting", StringComparison.OrdinalIgnoreCase));

    public string? SourceLine
    {
        get
        {
            if (!string.Equals(SourceKind, "email", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(SourceMatchReason))
            {
                return null;
            }

            var why = SourceMatchReason switch
            {
                "name" => "matched project name",
                "code" => "matched project code",
                "alias" => "matched alias",
                "name_token" => "matched name token",
                "operator" => "assigned by you",
                "explicit" => "explicit project link",
                null or "" => null,
                _ => $"matched via {SourceMatchReason}",
            };
            var conf = SourceConfidence is { } c ? $" · {c:P0}" : string.Empty;
            return why is null
                ? $"from email{conf}"
                : $"from email · {why}{conf}";
        }
    }

    public string DisplayLine =>
        string.IsNullOrWhiteSpace(Status)
            ? (string.IsNullOrWhiteSpace(NextAction) ? Title : $"{Title} · {NextAction}")
            : string.IsNullOrWhiteSpace(NextAction)
                ? $"{StatusLabel} · {Title}"
                : $"{StatusLabel} · {Title} · {NextAction}";

    public string StatusLabel => Status switch
    {
        "blocked" => "Blocked",
        "waiting" => "Waiting",
        "active" => "Active",
        "not_started" => "New",
        _ => Status,
    };

    public bool IsImportant => Priority == 1;

    public bool IsUrgentEffective(DateTimeOffset nowUtc, TimeSpan dueWindow)
    {
        if (Urgency is 1)
        {
            return true;
        }

        if (Urgency is 0)
        {
            return false;
        }

        if (string.Equals(Status, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(DueAt) || !DateTimeOffset.TryParse(DueAt, out var due))
        {
            return false;
        }

        return due <= nowUtc + dueWindow;
    }
}

public sealed class LimboNoteVm
{
    public string Id { get; set; } = string.Empty;

    public string OriginalText { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;

    public string? SuggestionId { get; set; }

    public string? SuggestionSummary { get; set; }

    public bool HasSuggestion => !string.IsNullOrWhiteSpace(SuggestionId) && !string.IsNullOrWhiteSpace(SuggestionSummary);
}

public sealed class ProjectContextVm
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string? Code { get; set; }

    public ProjectDossierVm? Dossier { get; set; }

    public bool DossierEmpty { get; set; } = true;

    public IList<ProjectAliasVm> Aliases { get; set; } = [];

    public IList<CellLineVm> Tasks { get; set; } = [];

    public IList<CellLineVm> CompletedTasks { get; set; } = [];

    public IList<ContextNoteVm> Notes { get; set; } = [];

    public IList<ContextBlockerVm> Blockers { get; set; } = [];

    public IList<ContextContactVm> Contacts { get; set; } = [];

    public IList<string> Meetings { get; set; } = [];

    public IList<ContextSuggestionVm> Suggestions { get; set; } = [];

    public IList<ContextFileVm> Files { get; set; } = [];
}

public sealed class ProjectDossierVm
{
    public int Version { get; set; } = 1;

    public string? Address { get; set; }

    public string? OwnerClient { get; set; }

    public string? Phase { get; set; }

    public string? Portfolio { get; set; }

    public string? LinkedFolder { get; set; }

    public IList<string> CurrentPriorities { get; set; } = [];

    public IList<string> MailboxSources { get; set; } = [];

    public IList<string> CalendarSources { get; set; } = [];

    public bool Empty { get; set; } = true;
}

public sealed class ProjectAliasVm
{
    public string Id { get; set; } = string.Empty;

    public string Alias { get; set; } = string.Empty;
}

public sealed class ContextContactVm
{
    public string PersonId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? OrganizationName { get; set; }
}

public sealed class ContextNoteVm
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class ContextBlockerVm
{
    public string Id { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? TaskId { get; set; }

    public string? CreatedAt { get; set; }
}

public sealed class ContextSuggestionVm
{
    public string Id { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;
}

/// <summary>Dependency edges around one task, split by direction.</summary>
public sealed class TaskLinksVm
{
    /// <summary>Tasks this one is waiting on.</summary>
    public IList<TaskLinkVm> WaitingOn { get; set; } = [];

    /// <summary>Tasks that are waiting on this one.</summary>
    public IList<TaskLinkVm> Feeds { get; set; } = [];

    public int Count => WaitingOn.Count + Feeds.Count;
}

public sealed class TaskLinkVm
{
    public string DependencyId { get; set; } = string.Empty;

    public string DependencyType { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? NextAction { get; set; }

    public string? Expects { get; set; }

    public string? Reason { get; set; }

    public string? CreatedAt { get; set; }

    public string? FollowUpAt { get; set; }

    public string? Cadence { get; set; }

    public string? EvidenceRef { get; set; }

    public string? SatisfiedAt { get; set; }

    /// <summary>The counterpart task is complete, or the edge was cleared with evidence.</summary>
    public bool Satisfied { get; set; }
}

public sealed class PendingSuggestionVm
{
    public string Id { get; set; } = string.Empty;

    public string SuggestionType { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? ProjectId { get; set; }

    public string? TaskId { get; set; }

    public string? PayloadJson { get; set; }

    public string? GroupKey { get; set; }

    public double? Confidence { get; set; }

    public string TypeLabel => SuggestionType switch
    {
        "merge_into_task" => "Merge into task",
        "disambiguate_email_claim" => "Pick project",
        "assign_to_project" or "assign_project" => "Assign to project",
        "link_tasks" => "Link tasks",
        "review_limbo" => "Review limbo",
        "dependency_ready" => "Dependency ready",
        "reporting_relationship" => "Reporting link",
        "link_contact" => "Link contact",
        "contact_merge" => "Merge contacts",
        _ => string.IsNullOrWhiteSpace(SuggestionType) ? "Suggestion" : SuggestionType.Replace('_', ' '),
    };

    public string ConfidenceLabel => Confidence is null
        ? "no score"
        : $"{Confidence.Value:P0}";

    public string MetaLine => $"{TypeLabel} · {ConfidenceLabel}";
}

public sealed class SuggestionBatchDecideResult
{
    public int Accepted { get; set; }

    public int Rejected { get; set; }

    public int Expired { get; set; }

    public int Failed { get; set; }

    public IReadOnlyList<SuggestionBatchDecideItemVm> Results { get; set; } = [];
}

public sealed class SuggestionBatchDecideItemVm
{
    public string Id { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string? Error { get; set; }
}

public sealed class TaskEmailThreadVm
{
    public string Id { get; set; } = string.Empty;

    public string TaskId { get; set; } = string.Empty;

    public string ConversationId { get; set; } = string.Empty;

    public string? AnchorEmailId { get; set; }

    public string? Subject { get; set; }

    public string? LatestSentAt { get; set; }

    public int MessageCount { get; set; }

    public string DisplayLine =>
        string.IsNullOrWhiteSpace(Subject)
            ? $"{MessageCount} message(s) in thread"
            : $"{Subject} · {MessageCount} msg";
}

public sealed class ContextFileVm
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

public sealed class CaptureResponseVm
{
    public string NoteId { get; set; } = string.Empty;

    public string? TaskId { get; set; }

    public string OriginalText { get; set; } = string.Empty;

    public string? ProjectId { get; set; }

    public bool IsLimbo { get; set; }
}

public sealed class WorkbenchAgentBubbleVm
{
    public string RoleLabel { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

public sealed class CustomFieldRowVm
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string FieldType { get; set; } = "text";

    public string Value { get; set; } = string.Empty;
}
