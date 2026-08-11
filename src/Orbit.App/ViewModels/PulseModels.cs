namespace Orbit_App.ViewModels;

public sealed class PulseVm
{
    public string? DayBrief { get; set; }

    public string? HermesHint { get; set; }

    public string GeneratedAt { get; set; } = string.Empty;

    public bool BriefIsSynthetic { get; set; }

    public IList<PulseConcernVm> Concerns { get; set; } = [];

    public IList<PulseUnmatchedMailVm> UnmatchedMail { get; set; } = [];

    public PulseBriefingVm? Briefing { get; set; }

    public OperatorRunVm? LastOperatorRun { get; set; }

    public string GeneratedAtDisplay =>
        DateTimeOffset.TryParse(GeneratedAt, out var when)
            ? when.LocalDateTime.ToString("g")
            : GeneratedAt;
}

public sealed class PulseBriefingVm
{
    public IList<PulseBriefingMeetingVm> UpcomingMeetings { get; set; } = [];

    public IList<PulseBriefingActionVm> TopActions { get; set; } = [];

    public IList<PulseBriefingWaitingVm> WaitingOn { get; set; } = [];

    public IList<PulseBriefingAlertVm> Alerts { get; set; } = [];

    public IList<PulseBriefingChangeVm> RecentChanges { get; set; } = [];

    public long ChangeCursor { get; set; }
}

public sealed class PulseBriefingMeetingVm
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? StartsAt { get; set; }

    public string? SourceName { get; set; }

    public string Line
    {
        get
        {
            var when = DateTimeOffset.TryParse(StartsAt, out var t)
                ? t.LocalDateTime.ToString("ddd g")
                : StartsAt;
            var src = string.IsNullOrWhiteSpace(SourceName) ? null : SourceName;
            return src is null ? $"{when} · {Title}" : $"{when} · {Title} ({src})";
        }
    }
}

public sealed class PulseBriefingActionVm
{
    public string TaskId { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? NextAction { get; set; }

    public string Line =>
        string.IsNullOrWhiteSpace(NextAction)
            ? $"{ProjectName}: {Title}"
            : $"{ProjectName}: {NextAction}";
}

public sealed class PulseBriefingWaitingVm
{
    public string TaskId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public int AgeHours { get; set; }

    public string Line
    {
        get
        {
            var age = AgeHours >= 48
                ? $"{AgeHours / 24}d"
                : $"{AgeHours}h";
            return $"{ProjectName}: {Title} · {Status} · {age}";
        }
    }
}

public sealed class PulseBriefingAlertVm
{
    public string Kind { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public string? ProjectId { get; set; }
}

public sealed class PulseBriefingChangeVm
{
    public long Revision { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public string EntityId { get; set; } = string.Empty;

    public string ChangeKind { get; set; } = string.Empty;

    public string? SourceEvent { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string Line
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(SourceEvent) ? ChangeKind : SourceEvent!;
            return $"{EntityType} · {label}";
        }
    }
}

public sealed class OperatorRunVm
{
    public string Id { get; set; } = string.Empty;

    public string TriggerKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? BriefingSummary { get; set; }

    public string? ErrorText { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string? CompletedAt { get; set; }

    public string WhenDisplay
    {
        get
        {
            var raw = CompletedAt ?? CreatedAt;
            return DateTimeOffset.TryParse(raw, out var when)
                ? when.LocalDateTime.ToString("g")
                : raw;
        }
    }
}

public sealed class PulseConcernVm
{
    public string TaskId { get; set; } = string.Empty;

    public string ProjectId { get; set; } = string.Empty;

    public string ProjectName { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? NextAction { get; set; }

    public string? BodyExcerpt { get; set; }

    public string UpdatedAt { get; set; } = string.Empty;

    public string? SourceKind { get; set; }

    public double? SourceConfidence { get; set; }

    public string? SourceMatchReason { get; set; }

    public string StatusLabel => Status switch
    {
        "blocked" => "Blocked",
        "waiting" => "Waiting",
        "active" => "Active",
        "not_started" => "New",
        _ => Status,
    };

    public string SubtitleLine =>
        string.IsNullOrWhiteSpace(NextAction)
            ? $"{ProjectName} · {StatusLabel}"
            : $"{ProjectName} · {NextAction}";

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
}

public sealed class PulseUnmatchedMailVm
{
    public string SuggestionId { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string? EmailId { get; set; }

    public string? Subject { get; set; }

    public string? Snippet { get; set; }

    public double? Confidence { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string CaptionLine
    {
        get
        {
            var conf = Confidence is { } c ? $" · {c:P0}" : string.Empty;
            if (!string.IsNullOrWhiteSpace(Snippet))
            {
                return $"{Snippet}{conf}";
            }

            return $"Needs project{conf}";
        }
    }
}

public sealed class ProjectMergePreviewVm
{
    public string SourceProjectId { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public string TargetProjectId { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public int TaskCount { get; set; }

    public int NoteCount { get; set; }

    public int WorkstreamCount { get; set; }

    public int FileLinkCount { get; set; }

    public int EmailLinkCount { get; set; }

    public int ContactLinkCount { get; set; }

    public int AliasCount { get; set; }

    public int BlockerCount { get; set; }

    public int FolderCount { get; set; }

    public IReadOnlyList<string> Warnings { get; set; } = [];

    public string CountsLine =>
        $"{TaskCount} tasks · {NoteCount} notes · {FileLinkCount} files · {EmailLinkCount} emails · {ContactLinkCount} contacts · {AliasCount} aliases";
}

public sealed class ProjectMergeResultVm
{
    public string SourceProjectId { get; set; } = string.Empty;

    public string SourceName { get; set; } = string.Empty;

    public string TargetProjectId { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public bool ArchivedSource { get; set; }

    public string MergedAt { get; set; } = string.Empty;
}

public sealed class ConcernVm
{
    public string TaskId { get; set; } = string.Empty;

    public string? ProjectId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? NextAction { get; set; }

    public string? Body { get; set; }

    public string? ProjectName { get; set; }

    public string StatusLabel => Status switch
    {
        "blocked" => "Blocked",
        "waiting" => "Waiting",
        "active" => "Active",
        "not_started" => "New",
        _ => Status,
    };
}

public sealed class OrbitVm
{
    public bool IgnitionCompleted { get; set; }

    public IList<OrbitProjectVm> Projects { get; set; } = [];
}

public sealed class OrbitProjectVm
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Summary { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool InOrbit { get; set; }

    public int OpenConcernCount { get; set; }

    public string? TopNextAction { get; set; }

    public bool DossierEmpty { get; set; }

    public bool MissingNextAction { get; set; }

    public string SummaryLine
    {
        get
        {
            var flags = new List<string>();
            if (DossierEmpty)
            {
                flags.Add("empty dossier");
            }

            if (MissingNextAction)
            {
                flags.Add("needs next action");
            }

            if (OpenConcernCount > 0)
            {
                var next = string.IsNullOrWhiteSpace(TopNextAction) ? null : TopNextAction;
                var baseLine = next is null
                    ? $"{OpenConcernCount} open"
                    : $"{OpenConcernCount} open · {next}";
                return flags.Count == 0 ? baseLine : $"{baseLine} · {string.Join(" · ", flags)}";
            }

            if (flags.Count > 0)
            {
                return string.Join(" · ", flags);
            }

            return string.IsNullOrWhiteSpace(Summary)
                ? (InOrbit ? "In orbit · ask Hermes for a next step" : Status)
                : Summary!;
        }
    }
}

public sealed class IgnitionProjectVm
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Created { get; set; }

    public string? HomeFolderPath { get; set; }

    public string? Error { get; set; }

    public string DisplayLine =>
        string.IsNullOrWhiteSpace(Error)
            ? Created
                ? $"{Name} · created · {HomeFolderPath ?? "no folder"}"
                : $"{Name} · linked · {HomeFolderPath ?? "no folder"}"
            : $"{Name} · {Error}";
}

public sealed class IgnitionConfirmVm
{
    public bool IgnitionCompleted { get; set; }

    public string? SnapshotId { get; set; }

    public string? DayBrief { get; set; }

    public string? CreatedAt { get; set; }
}
