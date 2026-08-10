namespace Orbit_App.ViewModels;

public sealed class PulseVm
{
    public string? DayBrief { get; set; }

    public string? HermesHint { get; set; }

    public string GeneratedAt { get; set; } = string.Empty;

    public bool BriefIsSynthetic { get; set; }

    public IList<PulseConcernVm> Concerns { get; set; } = [];

    public OperatorRunVm? LastOperatorRun { get; set; }

    public string GeneratedAtDisplay =>
        DateTimeOffset.TryParse(GeneratedAt, out var when)
            ? when.LocalDateTime.ToString("g")
            : GeneratedAt;
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

    public string SummaryLine
    {
        get
        {
            if (OpenConcernCount > 0)
            {
                var next = string.IsNullOrWhiteSpace(TopNextAction) ? null : TopNextAction;
                return next is null
                    ? $"{OpenConcernCount} open"
                    : $"{OpenConcernCount} open · {next}";
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
