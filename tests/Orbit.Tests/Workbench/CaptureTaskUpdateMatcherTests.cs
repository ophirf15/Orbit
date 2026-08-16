using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class CaptureTaskUpdateMatcherTests
{
    [Fact]
    public void Rank_EmptyCapture_ReturnsEmpty()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "   ",
            [new CaptureTaskCandidate("t1", "Call vendor about permits")]);

        Assert.Empty(ranked);
    }

    [Fact]
    public void Rank_ExactTitle_IsHigh()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "Call vendor about permits",
            [
                new CaptureTaskCandidate("t1", "Call vendor about permits"),
                new CaptureTaskCandidate("t2", "Unrelated landscaping bid"),
            ]);

        Assert.NotEmpty(ranked);
        Assert.Equal("t1", ranked[0].TaskId);
        Assert.Equal(CaptureTaskMatchBand.High, ranked[0].Band);
        Assert.Equal("exact_title", ranked[0].Reason);
        Assert.True(ranked[0].Score >= CaptureTaskUpdateMatcher.HighThreshold);
    }

    [Fact]
    public void Rank_TitleContainment_IsHigh()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "MetroFiber permit package follow-up",
            [
                new CaptureTaskCandidate(
                    "t1",
                    "MetroFiber permit package follow-up — waiting on bond paperwork"),
            ]);

        Assert.Single(ranked);
        Assert.Equal(CaptureTaskMatchBand.High, ranked[0].Band);
        Assert.Equal("title_containment", ranked[0].Reason);
    }

    [Fact]
    public void Rank_StrongTitleTokens_RanksAboveWeak()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "Follow up MetroFiber permit package bond",
            [
                new CaptureTaskCandidate("weak", "Buy office snacks"),
                new CaptureTaskCandidate(
                    "strong",
                    "MetroFiber permit follow-up",
                    NextAction: "Call about bond paperwork",
                    Body: "Waiting on signed bond from MetroFiber."),
            ]);

        Assert.NotEmpty(ranked);
        Assert.Equal("strong", ranked[0].TaskId);
        Assert.True(ranked[0].Score > CaptureTaskUpdateMatcher.MediumThreshold);
    }

    [Fact]
    public void Rank_NextActionOverlap_CanReachMedium()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "Grant returned the signed PMA today",
            [
                new CaptureTaskCandidate(
                    "t1",
                    "Grant PMA paperwork",
                    NextAction: "Waiting on Grant to return signed PMA"),
            ]);

        Assert.NotEmpty(ranked);
        Assert.Equal("t1", ranked[0].TaskId);
        Assert.True(ranked[0].Band is CaptureTaskMatchBand.Medium or CaptureTaskMatchBand.High);
        Assert.True(ranked[0].Score >= CaptureTaskUpdateMatcher.MediumThreshold);
    }

    [Fact]
    public void Rank_UnrelatedCapture_StaysLowOrAbsent()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "Schedule dentist cleaning next month",
            [
                new CaptureTaskCandidate("t1", "MetroFiber permit package"),
                new CaptureTaskCandidate("t2", "Order lumber for framing"),
            ]);

        Assert.True(
            ranked.Count == 0
            || ranked.All(m => m.Band == CaptureTaskMatchBand.Low));
    }

    [Fact]
    public void BandFor_Thresholds()
    {
        Assert.Equal(CaptureTaskMatchBand.High, CaptureTaskUpdateMatcher.BandFor(0.85));
        Assert.Equal(CaptureTaskMatchBand.High, CaptureTaskUpdateMatcher.BandFor(0.99));
        Assert.Equal(CaptureTaskMatchBand.Medium, CaptureTaskUpdateMatcher.BandFor(0.55));
        Assert.Equal(CaptureTaskMatchBand.Medium, CaptureTaskUpdateMatcher.BandFor(0.70));
        Assert.Equal(CaptureTaskMatchBand.Low, CaptureTaskUpdateMatcher.BandFor(0.54));
        Assert.Equal(CaptureTaskMatchBand.Low, CaptureTaskUpdateMatcher.BandFor(0.10));
    }

    [Fact]
    public void Decide_High_ProposesSingleUpdate()
    {
        var ranked = CaptureTaskUpdateMatcher.Rank(
            "Call vendor about permits",
            [new CaptureTaskCandidate("t1", "Call vendor about permits")]);

        var decision = CaptureTaskUpdateMatcher.Decide(ranked);

        Assert.Equal(CaptureTaskMatchIntent.ProposeUpdate, decision.Intent);
        Assert.NotNull(decision.Primary);
        Assert.Equal("t1", decision.Primary!.TaskId);
    }

    [Fact]
    public void Decide_Medium_ShowsUpToThreeAlternatives()
    {
        // Force medium band via synthetic ranked list (Decide is band-driven).
        var ranked = new List<CaptureTaskMatch>
        {
            new("a", "Alpha permit follow-up", 0.70, CaptureTaskMatchBand.Medium, "title_tokens"),
            new("b", "Alpha bond paperwork", 0.62, CaptureTaskMatchBand.Medium, "title_tokens"),
            new("c", "Alpha city filing", 0.58, CaptureTaskMatchBand.Medium, "title_tokens"),
            new("d", "Noise", 0.56, CaptureTaskMatchBand.Medium, "weak_overlap"),
        };

        var decision = CaptureTaskUpdateMatcher.Decide(ranked);

        Assert.Equal(CaptureTaskMatchIntent.ShowAlternatives, decision.Intent);
        Assert.Equal(3, decision.Alternatives.Count);
        Assert.Equal("a", decision.Alternatives[0].TaskId);
    }

    [Fact]
    public void Decide_LowOrEmpty_CreateNew()
    {
        Assert.Equal(
            CaptureTaskMatchIntent.CreateNew,
            CaptureTaskUpdateMatcher.Decide([]).Intent);

        var low = new List<CaptureTaskMatch>
        {
            new("t1", "Something", 0.40, CaptureTaskMatchBand.Low, "weak_overlap"),
        };
        Assert.Equal(
            CaptureTaskMatchIntent.CreateNew,
            CaptureTaskUpdateMatcher.Decide(low).Intent);
    }

    [Fact]
    public void AppendToBody_PreservesOriginalWordingAndExistingBody()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var capture = "Grant sent draft PMA — still needs signature";
        var merged = CaptureTaskUpdateAppender.AppendToBody(
            "Already tracked waiting on Grant.",
            capture,
            now);

        Assert.StartsWith("Already tracked waiting on Grant.", merged, StringComparison.Ordinal);
        Assert.Contains("From capture (2026-08-12):", merged, StringComparison.Ordinal);
        Assert.Contains(capture, merged, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendToBody_EmptyBody_StartsWithStamp()
    {
        var now = new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);
        var merged = CaptureTaskUpdateAppender.AppendToBody(null, "New note text", now);

        Assert.Equal("From capture (2026-08-12): New note text", merged);
    }
}
