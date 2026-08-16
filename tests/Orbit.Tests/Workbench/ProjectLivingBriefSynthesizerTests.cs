using Orbit.Core.Data;
using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class ProjectLivingBriefSynthesizerTests
{
    [Fact]
    public void Synthesize_BuildsObjectiveCommitmentsRisksPeopleAndDates()
    {
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(new ProjectLivingBriefSnapshot
        {
            ProjectName = "Harbor Court",
            OpenTasks =
            [
                new LivingBriefTaskItem("Roof bid", TaskStatuses.Active, "Close roof bid", "2026-08-20"),
                new LivingBriefTaskItem("PMA", TaskStatuses.Waiting, "Chase signed PMA", null, "Grant"),
                new LivingBriefTaskItem("CO chase", TaskStatuses.Blocked, "Waiting on city", null),
            ],
            OpenBlockers = [new LivingBriefBlockerItem("Permit bond stalled", "open")],
            Contacts =
            [
                new LivingBriefContactItem("Pat Vendor", "GC", null, "p1"),
            ],
            UpcomingMeetings = [new LivingBriefMeetingItem("Owner sync", "2026-08-15T17:00:00Z")],
            RecentNotes = [new LivingBriefNoteItem("Spoke with Grant about PMA timing")],
        });

        Assert.True(proposal.HasContent);
        Assert.Contains("Objective:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Close roof bid", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Commitments:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Risks / blockers:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Permit bond stalled", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Waiting:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Grant", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("People:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Pat Vendor", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Contains("Upcoming:", proposal.SummaryText, StringComparison.Ordinal);
        Assert.Equal(1, proposal.CurrentPriorities.Count(p => p.Contains("Roof bid", StringComparison.Ordinal)));
        Assert.Single(proposal.CriticalContacts);
    }

    [Fact]
    public void Baseline_FillsBlankSummary_AndEmptyDossierSlots()
    {
        var snapshot = new ProjectLivingBriefSnapshot
        {
            ProjectName = "Empty Co",
            CurrentSummary = null,
            DossierEmpty = true,
            OpenTasks =
            [
                new LivingBriefTaskItem("Close roof", TaskStatuses.Active, "Send final numbers", null),
            ],
            Contacts = [new LivingBriefContactItem("Alex", "PM")],
        };
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(snapshot);
        var merge = ProjectLivingBriefSynthesizer.Merge(snapshot, proposal, LivingBriefApplyMode.Baseline);

        Assert.True(merge.WriteSummary);
        Assert.False(string.IsNullOrWhiteSpace(merge.Summary));
        Assert.True(merge.WritePriorities);
        Assert.True(merge.WriteContacts);
        Assert.Contains("Alex", merge.Contacts[0].DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_DoesNotOverwriteOperatorSummary()
    {
        var snapshot = new ProjectLivingBriefSnapshot
        {
            ProjectName = "Harbor",
            CurrentSummary = "Operator owned prose — keep this.",
            DossierEmpty = true,
            OpenTasks =
            [
                new LivingBriefTaskItem("Task A", TaskStatuses.Active, "Do the thing", null),
            ],
        };
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(snapshot);
        var merge = ProjectLivingBriefSynthesizer.Merge(snapshot, proposal, LivingBriefApplyMode.Baseline);

        Assert.False(merge.WriteSummary);
        Assert.True(merge.WritePriorities);
    }

    [Fact]
    public void Refresh_AppendsAutoBrief_PreservingOperatorProse()
    {
        var clock = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new ProjectLivingBriefSnapshot
        {
            ProjectName = "Harbor",
            CurrentSummary = "Operator owned prose — keep this.",
            DossierEmpty = false,
            ExistingPriorities = ["Already set"],
            HasCriticalContacts = true,
            OpenTasks =
            [
                new LivingBriefTaskItem("Task A", TaskStatuses.Active, "Do the thing", null),
            ],
        };
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(snapshot);
        var merge = ProjectLivingBriefSynthesizer.Merge(snapshot, proposal, LivingBriefApplyMode.Refresh, clock);

        Assert.True(merge.WriteSummary);
        Assert.StartsWith("Operator owned prose — keep this.", merge.Summary, StringComparison.Ordinal);
        Assert.Contains("Auto brief (2026-08-12):", merge.Summary, StringComparison.Ordinal);
        Assert.Contains("Do the thing", merge.Summary, StringComparison.Ordinal);
        Assert.False(merge.WriteContacts);
    }

    [Fact]
    public void Refresh_ReplacesExistingAutoBriefSection()
    {
        var clock = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        var existing =
            "Operator owned.\n\n---\nAuto brief (2026-08-12):\nObjective: Old auto text";
        var merged = ProjectLivingBriefSynthesizer.MergeAutoBriefSection(
            existing,
            "Objective: New auto text",
            clock);

        Assert.StartsWith("Operator owned.", merged, StringComparison.Ordinal);
        Assert.DoesNotContain("Old auto text", merged, StringComparison.Ordinal);
        Assert.Contains("Auto brief (2026-08-13):", merged, StringComparison.Ordinal);
        Assert.Contains("New auto text", merged, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_SkipsWhenNothingToSay()
    {
        var snapshot = EmptyProjectSnapshot();
        var proposal = ProjectLivingBriefSynthesizer.Synthesize(snapshot);
        var merge = ProjectLivingBriefSynthesizer.Merge(snapshot, proposal, LivingBriefApplyMode.Baseline);

        Assert.False(proposal.HasContent);
        Assert.False(merge.Changed);
    }

    [Fact]
    public void NeedsBaseline_WhenSummaryBlankOrDossierEmpty()
    {
        Assert.True(ProjectLivingBriefSynthesizer.NeedsBaseline(null, dossierEmpty: false));
        Assert.True(ProjectLivingBriefSynthesizer.NeedsBaseline("  ", dossierEmpty: false));
        Assert.True(ProjectLivingBriefSynthesizer.NeedsBaseline("Has text", dossierEmpty: true));
        Assert.False(ProjectLivingBriefSynthesizer.NeedsBaseline("Has text", dossierEmpty: false));
    }

    private static ProjectLivingBriefSnapshot EmptyProjectSnapshot() => new()
    {
        ProjectName = "Empty Co",
        CurrentSummary = null,
        DossierEmpty = true,
    };
}
