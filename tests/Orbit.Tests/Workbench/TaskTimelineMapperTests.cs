using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class TaskTimelineMapperTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_Empty_ReturnsEmpty()
    {
        Assert.Empty(TaskTimelineMapper.Map(null, Now));
        Assert.Empty(TaskTimelineMapper.Map([], Now));
    }

    [Fact]
    public void Map_FormatsKinds_AndSortsNewestFirst()
    {
        var facts = new[]
        {
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Created,
                At = Now.AddDays(-10).ToString("o"),
                DedupeKey = "created",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Status,
                At = Now.AddHours(-2).ToString("o"),
                StatusLabel = "Waiting",
                DedupeKey = "status-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Note,
                At = Now.AddMinutes(-30).ToString("o"),
                Summary = "  Called vendor about  permit  ",
                DedupeKey = "note-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.EmailLinked,
                At = Now.AddHours(-5).ToString("o"),
                Summary = "Re: Harbor quote",
                DedupeKey = "email-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.BlockerSet,
                At = Now.AddDays(-1).ToString("o"),
                Summary = "Awaiting permit",
                DedupeKey = "blocker-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.WaitingOnLinked,
                At = Now.AddHours(-8).ToString("o"),
                Summary = "Permit submission",
                Detail = "line count",
                DedupeKey = "wait-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.BriefUpdate,
                At = Now.AddMinutes(-10).ToString("o"),
                Summary = "Hermes",
                SourceEvent = "operator.briefing",
                DedupeKey = "brief-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.FileLinked,
                At = Now.AddHours(-12).ToString("o"),
                Summary = "metrofiber.pdf",
                DedupeKey = "file-1",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.BlockerCleared,
                At = Now.AddHours(-1).ToString("o"),
                Summary = "Awaiting permit",
                DedupeKey = "blocker-clear-1",
            },
        };

        var lines = TaskTimelineMapper.Map(facts, Now);

        Assert.Equal(9, lines.Count);
        Assert.Equal("Brief updated · Hermes", lines[0].Text);
        Assert.Equal("10m ago", lines[0].WhenLabel);
        Assert.Equal("Note · Called vendor about permit", lines[1].Text);
        Assert.Equal("Blocker cleared · Awaiting permit", lines[2].Text);
        Assert.Equal("Status → Waiting", lines[3].Text);
        Assert.Equal("Email · Re: Harbor quote", lines[4].Text);
        Assert.Equal("Waiting on · Permit submission (line count)", lines[5].Text);
        Assert.Equal("File · metrofiber.pdf", lines[6].Text);
        Assert.Equal("Blocker set · Awaiting permit", lines[7].Text);
        Assert.Equal("Task created", lines[^1].Text);
    }

    [Fact]
    public void Map_DedupesByKey()
    {
        var facts = new[]
        {
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Status,
                At = Now.ToString("o"),
                StatusLabel = "Active",
                DedupeKey = "same",
            },
            new TaskTimelineFact
            {
                Kind = TaskTimelineKinds.Status,
                At = Now.ToString("o"),
                StatusLabel = "Active",
                DedupeKey = "same",
            },
        };

        var lines = TaskTimelineMapper.Map(facts, Now);
        Assert.Single(lines);
    }

    [Fact]
    public void Map_Change_OperatorBriefing()
    {
        var lines = TaskTimelineMapper.Map(
            [
                new TaskTimelineFact
                {
                    Kind = TaskTimelineKinds.Change,
                    At = Now.AddHours(-3).ToString("o"),
                    SourceEvent = "operator.briefing",
                    Summary = "stalled",
                },
            ],
            Now);

        Assert.Single(lines);
        Assert.Equal("Hermes briefing · stalled", lines[0].Text);
        Assert.Equal("3h ago", lines[0].WhenLabel);
    }

    [Fact]
    public void TakeRecent_CapsNewest()
    {
        var lines = TaskTimelineMapper.Map(
            [
                new TaskTimelineFact { Kind = TaskTimelineKinds.Created, At = Now.AddDays(-2).ToString("o"), DedupeKey = "a" },
                new TaskTimelineFact { Kind = TaskTimelineKinds.Note, At = Now.AddHours(-1).ToString("o"), Summary = "n1", DedupeKey = "b" },
                new TaskTimelineFact { Kind = TaskTimelineKinds.Note, At = Now.AddMinutes(-5).ToString("o"), Summary = "n2", DedupeKey = "c" },
                new TaskTimelineFact { Kind = TaskTimelineKinds.Status, At = Now.AddMinutes(-1).ToString("o"), StatusLabel = "Active", DedupeKey = "d" },
            ],
            Now);

        var recent = TaskTimelineMapper.TakeRecent(lines, 2);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Status → Active", recent[0].Text);
        Assert.Equal("Note · n2", recent[1].Text);
    }

    [Fact]
    public void FormatWhen_RelativeBuckets()
    {
        Assert.Equal("1m ago", TaskTimelineMapper.FormatWhen(Now.AddMinutes(-1), Now));
        Assert.Equal("2h ago", TaskTimelineMapper.FormatWhen(Now.AddHours(-2), Now));
        Assert.Equal("24h ago", TaskTimelineMapper.FormatWhen(Now.AddDays(-1), Now));
        Assert.Equal("Yesterday", TaskTimelineMapper.FormatWhen(Now.AddHours(-40), Now));
        Assert.Equal("5d ago", TaskTimelineMapper.FormatWhen(Now.AddDays(-5), Now));
    }
}
