using Orbit.Core.Data;
using Orbit.Core.Pulse;

namespace Orbit.Tests.Pulse;

public sealed class AttentionReasonClassifierTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Classify_Blocked_ReturnsBlocked()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Blocked,
            nextAction: "Call vendor",
            sourceKind: null,
            updatedAt: Now.AddDays(-5).ToString("o"),
            now: Now);

        Assert.Equal("Blocked", label);
    }

    [Fact]
    public void Classify_WaitingAged_ReturnsWaitingSeveralDays()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Chase reply",
            sourceKind: null,
            updatedAt: Now.AddHours(-72).ToString("o"),
            now: Now);

        Assert.Equal("Waiting several days", label);
    }

    [Fact]
    public void Classify_WaitingAgedViaOverride_ReturnsWaitingSeveralDays()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: null,
            sourceKind: null,
            updatedAt: null,
            now: Now,
            ageHoursOverride: AttentionReasonClassifier.WaitingSeveralDaysHours);

        Assert.Equal("Waiting several days", label);
    }

    [Fact]
    public void Classify_WaitingFresh_ReturnsWaiting()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Reply",
            sourceKind: null,
            updatedAt: Now.AddHours(-6).ToString("o"),
            now: Now);

        Assert.Equal("Waiting", label);
    }

    [Fact]
    public void Classify_EmailSource_ReturnsNewEmail()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Active,
            nextAction: "Read thread",
            sourceKind: "email",
            updatedAt: Now.AddDays(-3).ToString("o"),
            now: Now);

        Assert.Equal("New email", label);
    }

    [Fact]
    public void Classify_RecentlyChanged_ReturnsRecentlyChanged()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Active,
            nextAction: "Ship fix",
            sourceKind: null,
            updatedAt: Now.AddHours(-2).ToString("o"),
            now: Now);

        Assert.Equal("Recently changed", label);
    }

    [Fact]
    public void Classify_MissingNextAction_ReturnsNeedsNextMove()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Active,
            nextAction: null,
            sourceKind: null,
            updatedAt: Now.AddDays(-4).ToString("o"),
            now: Now);

        Assert.Equal("Needs next move", label);
    }

    [Fact]
    public void Classify_ActiveWithNextAction_ReturnsWaitingOnYou()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Active,
            nextAction: "Draft reply",
            sourceKind: null,
            updatedAt: Now.AddDays(-3).ToString("o"),
            now: Now);

        Assert.Equal("Waiting on you", label);
    }

    [Fact]
    public void Classify_NotStartedMissingNext_ReturnsNeedsNextMove()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.NotStarted,
            nextAction: "   ",
            sourceKind: null,
            updatedAt: Now.AddDays(-10).ToString("o"),
            now: Now);

        Assert.Equal("Needs next move", label);
    }

    [Fact]
    public void Classify_BlockedBeatsEmailAndAge()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Blocked,
            nextAction: null,
            sourceKind: "email",
            updatedAt: Now.AddHours(-1).ToString("o"),
            now: Now);

        Assert.Equal("Blocked", label);
    }

    [Fact]
    public void Classify_WaitingBeatsEmail()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Ping",
            sourceKind: "email",
            updatedAt: Now.AddHours(-1).ToString("o"),
            now: Now);

        Assert.Equal("Waiting", label);
    }

    [Fact]
    public void Classify_WaitingFollowUpOverdue_ReturnsFollowUpDue()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Chase reply",
            sourceKind: null,
            updatedAt: Now.AddHours(-6).ToString("o"),
            now: Now,
            waitingFollowUpAt: Now.AddDays(-1).ToString("yyyy-MM-dd"));

        Assert.Equal("Follow-up due", label);
    }

    [Fact]
    public void Classify_WaitingSatisfiedIgnoresFollowUp()
    {
        var label = AttentionReasonClassifier.Classify(
            TaskStatuses.Waiting,
            nextAction: "Done",
            sourceKind: null,
            updatedAt: Now.AddHours(-6).ToString("o"),
            now: Now,
            waitingFollowUpAt: Now.AddDays(-2).ToString("yyyy-MM-dd"),
            waitingSatisfiedAt: Now.ToString("o"));

        Assert.Equal("Waiting", label);
    }

    [Fact]
    public void TryAgeHours_ParsesIsoTimestamp()
    {
        var hours = AttentionReasonClassifier.TryAgeHours(
            Now.AddHours(-30).ToString("o"),
            Now);

        Assert.Equal(30, hours);
    }

    [Fact]
    public void TryAgeHours_Invalid_ReturnsNull()
    {
        Assert.Null(AttentionReasonClassifier.TryAgeHours("not-a-date", Now));
        Assert.Null(AttentionReasonClassifier.TryAgeHours(null, Now));
    }
}
