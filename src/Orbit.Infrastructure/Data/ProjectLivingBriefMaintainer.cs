using Orbit.Core.Workbench;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Host-side living brief maintenance: synthesize from project context and persist non-destructively.
/// </summary>
public sealed class ProjectLivingBriefMaintainer
{
    private readonly ProjectContextReadStore _contexts;
    private readonly ProjectWriteStore _projects;

    public ProjectLivingBriefMaintainer(ProjectContextReadStore contexts, ProjectWriteStore projects)
    {
        _contexts = contexts;
        _projects = projects;
    }

    public sealed class ApplyResult
    {
        public required string ProjectId { get; init; }

        public bool Applied { get; init; }

        public bool SummaryUpdated { get; init; }

        public bool DossierUpdated { get; init; }

        public string? Summary { get; init; }

        public ProjectDossier? Dossier { get; init; }

        public bool DossierEmpty { get; init; } = true;

        public string? SkipReason { get; init; }
    }

    /// <summary>
    /// When summary is blank or dossier empty, fill baseline from open work.
    /// No-op when operator already has summary and dossier slots are populated, or when graph has nothing to say.
    /// </summary>
    public ApplyResult EnsureBaseline(string projectId) =>
        Apply(projectId, LivingBriefApplyMode.Baseline, requireNeedsBaseline: true);

    /// <summary>Explicit refresh: enrich empty slots and merge/append Auto brief into summary.</summary>
    public ApplyResult Refresh(string projectId) =>
        Apply(projectId, LivingBriefApplyMode.Refresh, requireNeedsBaseline: false);

    private ApplyResult Apply(string projectId, LivingBriefApplyMode mode, bool requireNeedsBaseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var id = projectId.Trim();
        var context = _contexts.GetContext(id);
        if (context is null)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }

        var dossier = context.Dossier ?? ProjectDossier.Empty();
        if (requireNeedsBaseline
            && !ProjectLivingBriefSynthesizer.NeedsBaseline(context.Summary, context.DossierEmpty))
        {
            return new ApplyResult
            {
                ProjectId = id,
                Applied = false,
                Summary = context.Summary,
                Dossier = context.DossierEmpty ? null : dossier,
                DossierEmpty = context.DossierEmpty,
                SkipReason = "summary_and_dossier_present",
            };
        }

        var snapshot = ToSnapshot(context, dossier);
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(snapshot);
        var merge = ProjectLivingBriefSynthesizer.Merge(snapshot, proposal, mode);
        if (!merge.Changed)
        {
            return new ApplyResult
            {
                ProjectId = id,
                Applied = false,
                Summary = context.Summary,
                Dossier = context.DossierEmpty ? null : dossier,
                DossierEmpty = context.DossierEmpty,
                SkipReason = proposal.HasContent ? "nothing_to_merge" : "no_signals",
            };
        }

        string? summary = context.Summary;
        var summaryUpdated = false;
        if (merge.WriteSummary)
        {
            var updated = _projects.Update(id, name: null, summary: merge.Summary);
            summary = updated.Summary;
            summaryUpdated = true;
        }

        ProjectDossier? nextDossier = dossier;
        var dossierUpdated = false;
        if (merge.WritePriorities || merge.WriteContacts)
        {
            var patch = new ProjectDossierPatch
            {
                CurrentPriorities = merge.WritePriorities ? merge.Priorities.ToList() : null,
                CriticalContacts = merge.WriteContacts
                    ? merge.Contacts.Select(c => new ProjectDossierContact
                    {
                        Name = c.DisplayName,
                        Role = string.IsNullOrWhiteSpace(c.Title) ? c.OrganizationName : c.Title,
                        PersonId = c.PersonId,
                    }).ToList()
                    : null,
            };
            nextDossier = _projects.UpdateDossier(id, patch);
            dossierUpdated = true;
        }
        else
        {
            try
            {
                nextDossier = _projects.GetDossier(id);
            }
            catch (ArgumentException)
            {
                nextDossier = dossier;
            }
        }

        var empty = nextDossier?.IsStructurallyEmpty ?? true;
        return new ApplyResult
        {
            ProjectId = id,
            Applied = true,
            SummaryUpdated = summaryUpdated,
            DossierUpdated = dossierUpdated,
            Summary = summary,
            Dossier = empty ? null : nextDossier,
            DossierEmpty = empty,
        };
    }

    internal static ProjectLivingBriefSnapshot ToSnapshot(ProjectContextRecord context, ProjectDossier dossier)
    {
        var openBlockers = context.Blockers
            .Where(b => !string.Equals(b.Status, "cleared", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(b.Status, "resolved", StringComparison.OrdinalIgnoreCase))
            .Select(b => new LivingBriefBlockerItem(b.Summary, b.Status))
            .ToList();

        return new ProjectLivingBriefSnapshot
        {
            ProjectName = context.Name,
            CurrentSummary = context.Summary,
            DossierEmpty = context.DossierEmpty,
            ExistingPriorities = dossier.CurrentPriorities ?? [],
            HasCriticalContacts = dossier.CriticalContacts.Count > 0,
            OpenTasks = context.Tasks.Select(t => new LivingBriefTaskItem(
                t.Title,
                t.Status,
                t.NextAction,
                t.DueAt,
                t.WaitingOnLabel)).ToList(),
            OpenBlockers = openBlockers,
            RecentNotes = context.Notes.Select(n => new LivingBriefNoteItem(n.OriginalText, n.CreatedAt)).ToList(),
            Contacts = context.Contacts.Select(c => new LivingBriefContactItem(
                c.DisplayName,
                c.Title,
                c.OrganizationName,
                c.PersonId)).ToList(),
            UpcomingMeetings = context.Meetings.Select(m => new LivingBriefMeetingItem(m.Title, m.StartsAt)).ToList(),
        };
    }
}
