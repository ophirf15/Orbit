using Orbit.Core.Data;
using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class OrbitTreeOperationalIndicatorsTests
{
    [Theory]
    [InlineData(TaskStatuses.Blocked, "Blocked", OrbitTreeOperationalIndicators.GlyphBlocked)]
    [InlineData(TaskStatuses.Waiting, "Waiting", OrbitTreeOperationalIndicators.GlyphWaiting)]
    [InlineData(TaskStatuses.Complete, "Completed", OrbitTreeOperationalIndicators.GlyphCompleted)]
    [InlineData(TaskStatuses.NotStarted, "Needs action", OrbitTreeOperationalIndicators.GlyphNeedsAction)]
    public void ForTaskStatus_MapsCoreStates(string status, string label, string glyph)
    {
        var indicator = OrbitTreeOperationalIndicators.ForTaskStatus(status, nextAction: "Do thing");
        Assert.Equal(label, indicator.Label);
        Assert.Equal(glyph, indicator.Glyph);
        Assert.False(string.IsNullOrWhiteSpace(indicator.Tooltip));
    }

    [Fact]
    public void ForTaskStatus_ActiveWithNext_IsActive()
    {
        var indicator = OrbitTreeOperationalIndicators.ForTaskStatus(TaskStatuses.Active, "Call vendor");
        Assert.Equal("Active", indicator.Label);
        Assert.Equal(OrbitTreeOperationalIndicators.GlyphActive, indicator.Glyph);
    }

    [Fact]
    public void ForTaskStatus_ActiveWithoutNext_NeedsAction()
    {
        var indicator = OrbitTreeOperationalIndicators.ForTaskStatus(TaskStatuses.Active, null);
        Assert.Equal("Needs action", indicator.Label);
        Assert.Equal(OrbitTreeOperationalIndicators.GlyphNeedsAction, indicator.Glyph);
    }

    [Fact]
    public void CountOpenTaskStatuses_IgnoresCompleteAndTalliesBlockedWaiting()
    {
        var (open, blocked, waiting) = OrbitTreeOperationalIndicators.CountOpenTaskStatuses(
        [
            TaskStatuses.Active,
            TaskStatuses.Blocked,
            TaskStatuses.Waiting,
            TaskStatuses.Waiting,
            TaskStatuses.Complete,
            TaskStatuses.NotStarted,
            TaskStatuses.Archived,
        ]);

        Assert.Equal(5, open);
        Assert.Equal(1, blocked);
        Assert.Equal(2, waiting);
    }

    [Fact]
    public void FormatProjectSubtitle_PrefersOpenBlockedWaiting()
    {
        Assert.Equal("No open tasks", OrbitTreeOperationalIndicators.FormatProjectSubtitle(0, 0, 0));
        Assert.Equal(
            "5 open · 2 blocked · 1 waiting",
            OrbitTreeOperationalIndicators.FormatProjectSubtitle(5, 2, 1));
        Assert.Equal(
            "3 open · 0 blocked · 1 waiting · 4 done",
            OrbitTreeOperationalIndicators.FormatProjectSubtitle(3, 0, 1, done: 4));
        Assert.Equal("2 done", OrbitTreeOperationalIndicators.FormatProjectSubtitle(0, 0, 0, done: 2));
    }
}
