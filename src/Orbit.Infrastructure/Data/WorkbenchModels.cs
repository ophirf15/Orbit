namespace Orbit.Infrastructure.Data;

public sealed class WorkbenchSnapshot
{
    public required IReadOnlyList<ProjectCellRecord> Cells { get; init; }

    public required IReadOnlyList<LimboNoteRecord> Limbo { get; init; }

    /// <summary>null = root workbench (project cells). Set when drilling into a project board.</summary>
    public WorkbenchScopeRecord? Scope { get; init; }
}

public sealed class WorkbenchScopeRecord
{
    public required string Kind { get; init; }

    public string? ProjectId { get; init; }

    public string? ProjectName { get; init; }
}

public sealed class ProjectCellRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Code { get; init; }

    public string? Summary { get; init; }

    public required string Status { get; init; }

    /// <summary>"project" at root; "task" inside a project board.</summary>
    public string CellKind { get; init; } = "project";

    public required IReadOnlyList<CellLineRecord> Lines { get; init; }

    public int OpenBlockerCount { get; init; }

    public string? TopBlockerSummary { get; init; }

    public string? UpcomingMeetingTitle { get; init; }

    public string? UpcomingMeetingStartsAt { get; init; }

    public int PendingSuggestionCount { get; init; }

    public string? RecentActivityAt { get; init; }

    /// <summary>Workbench stripe hex (#RRGGBB), or null for theme default.</summary>
    public string? AccentColor { get; init; }

    public int SortOrder { get; init; }

    public double? BoardX { get; init; }

    public double? BoardY { get; init; }

    public double? BoardW { get; init; }

    public double? BoardH { get; init; }

    public bool DossierEmpty { get; init; }

    public bool MissingNextAction { get; init; }
}

public sealed class CellLineRecord
{
    public required string TaskId { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public string? NextAction { get; init; }

    public string? Body { get; init; }

    public string? ProjectId { get; init; }

    public string? DueAt { get; init; }

    /// <summary>1 = Important, 0 = Less important, null = unset (treat as less).</summary>
    public int? Priority { get; init; }

    /// <summary>1 = Urgent, 0 = Less urgent, null = auto from due/blockers.</summary>
    public int? Urgency { get; init; }

    public string? SourceKind { get; init; }

    public double? SourceConfidence { get; init; }

    public string? SourceMatchReason { get; init; }

    public string? WaitingOnLabel { get; init; }

    public string? WaitingOnPersonId { get; init; }

    public string? WaitingOnOrganizationId { get; init; }

    public string? WaitingFollowUpAt { get; init; }

    public string? WaitingCadence { get; init; }

    public string? WaitingSatisfiedAt { get; init; }

    public string? WaitingEvidenceRef { get; init; }

    public string? CreatedAt { get; init; }

    public string? UpdatedAt { get; init; }
}

public sealed class LimboNoteRecord
{
    public required string Id { get; init; }

    public required string OriginalText { get; init; }

    public required string CreatedAt { get; init; }

    public string? SuggestionId { get; init; }

    public string? SuggestionSummary { get; init; }
}

public sealed class CaptureResult
{
    public required string NoteId { get; init; }

    public string? TaskId { get; init; }

    public required string OriginalText { get; init; }

    public string? ProjectId { get; init; }

    public bool IsLimbo { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ProjectContextRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Summary { get; init; }

    public string? Code { get; init; }

    public ProjectDossier? Dossier { get; init; }

    public bool DossierEmpty { get; init; } = true;

    public IReadOnlyList<ProjectAliasItem> Aliases { get; init; } = [];

    public required IReadOnlyList<CellLineRecord> Tasks { get; init; }

    public required IReadOnlyList<CellLineRecord> CompletedTasks { get; init; }

    public required IReadOnlyList<ContextNoteRecord> Notes { get; init; }

    public required IReadOnlyList<ContextBlockerRecord> Blockers { get; init; }

    public required IReadOnlyList<ContextContactRecord> Contacts { get; init; }

    public required IReadOnlyList<ContextMeetingRecord> Meetings { get; init; }

    public required IReadOnlyList<ContextSuggestionRecord> Suggestions { get; init; }

    public required IReadOnlyList<ContextFileRecord> Files { get; init; }
}

public sealed class ProjectAliasItem
{
    public required string Id { get; init; }

    public required string Alias { get; init; }
}

public sealed class ContextNoteRecord
{
    public required string Id { get; init; }

    public required string OriginalText { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ContextBlockerRecord
{
    public required string Id { get; init; }

    public required string Summary { get; init; }

    public required string Status { get; init; }

    public string? TaskId { get; init; }

    /// <summary>ISO timestamp from blockers.created_at (already on graph; no migration).</summary>
    public string? CreatedAt { get; init; }
}

public sealed class ContextContactRecord
{
    public required string PersonId { get; init; }

    public required string DisplayName { get; init; }

    public string? Title { get; init; }

    public string? OrganizationName { get; init; }
}

public sealed class ContextMeetingRecord
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? StartsAt { get; init; }
}

public sealed class ContextSuggestionRecord
{
    public required string Id { get; init; }

    public required string Summary { get; init; }

    public required string Status { get; init; }

    public string? NoteId { get; init; }
}

public sealed class ContextFileRecord
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Path { get; init; }

    public string? Extension { get; init; }
}
