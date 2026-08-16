using Orbit.Core.Pulse;

namespace Orbit.Tests.Pulse;

public sealed class WaitingOnStaleRankerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rank_PastFollowUp_OutranksAgeOnly()
    {
        var aged = new WaitingOnStaleRanker.WaitingSignal(
            "aged",
            "Vendor",
            FollowUpAt: null,
            Cadence: null,
            SatisfiedAt: null,
            UpdatedAt: Now.AddHours(-80).ToString("o"),
            Status: "waiting",
            AgeHours: 80);
        var overdue = new WaitingOnStaleRanker.WaitingSignal(
            "overdue",
            "Grant",
            FollowUpAt: "2026-08-10",
            Cadence: "3d",
            SatisfiedAt: null,
            UpdatedAt: Now.AddHours(-10).ToString("o"),
            Status: "waiting",
            AgeHours: 10);

        var ranked = WaitingOnStaleRanker.Rank([aged, overdue], Now, take: 8);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("overdue", ranked[0].Signal.TaskId);
        Assert.True(ranked[0].FollowUpOverdue);
        Assert.True(ranked[0].IsStale);
        Assert.True(ranked[0].StaleScore > ranked[1].StaleScore);
    }

    [Fact]
    public void Rank_ExcludesSatisfiedWaits()
    {
        var open = new WaitingOnStaleRanker.WaitingSignal(
            "open",
            "Grant",
            "2026-08-01",
            null,
            null,
            Now.AddDays(-5).ToString("o"),
            "waiting",
            120);
        var cleared = new WaitingOnStaleRanker.WaitingSignal(
            "cleared",
            "Grant",
            "2026-08-01",
            null,
            Now.ToString("o"),
            Now.AddDays(-5).ToString("o"),
            "waiting",
            120);

        var ranked = WaitingOnStaleRanker.Rank([open, cleared], Now);

        Assert.Equal("open", Assert.Single(ranked).Signal.TaskId);
    }

    [Fact]
    public void IsStale_FreshWaitWithoutFollowUp_False()
    {
        Assert.False(WaitingOnStaleRanker.IsStale(
            "waiting",
            followUpAt: null,
            satisfiedAt: null,
            updatedAt: Now.AddHours(-6).ToString("o"),
            now: Now));
    }

    [Fact]
    public void DefaultFollowUpAt_IsFutureDate()
    {
        var follow = WaitingOnStaleRanker.DefaultFollowUpAt(Now, days: 3);
        Assert.Equal("2026-08-15", follow);
    }
}
