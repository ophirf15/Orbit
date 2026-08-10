using Orbit.Core.Agent;

namespace Orbit.Tests.Agent;

public sealed class CaptureClarifyTests
{
    [Fact]
    public void Open_asks_a_question()
    {
        var result = CaptureClarify.Open("order metrofiber", "The Harbor Court");
        Assert.False(result.IsComplete);
        Assert.Contains('?', result.Message);
    }

    [Fact]
    public void Continue_finalizes_with_short_title_and_subtitle_from_replies()
    {
        var result = CaptureClarify.Continue(
            "order metrofiber",
            "The Harbor Court",
            [],
            "Account manager Jane — install Friday");
        Assert.True(result.IsComplete);
        Assert.Equal("Order Metrofiber", result.FinalTitle);
        Assert.Contains("Jane", result.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Clarified", result.Summary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Jane", result.FinalTitle!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComposeFinalTitle_does_not_append_conversation()
    {
        var title = CaptureClarify.ComposeFinalTitle(
            "order metrofiber",
            ["Account manager Jane", "Need install by Friday"]);
        Assert.Equal("Order Metrofiber", title);
        Assert.DoesNotContain("Jane", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Friday", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentComplete_reads_title_note_summary()
    {
        var parsed = CaptureClarify.TryParseAgentComplete(
            """
            DONE
            TITLE: Order MetroFiber at Harbor Court — Friday install
            NOTE: Spoke with Jane
            SUMMARY: User wants MetroFiber ordered; Jane is the contact; Friday install.
            """);
        Assert.NotNull(parsed);
        Assert.True(parsed!.IsComplete);
        Assert.Equal("Order MetroFiber at Harbor Court — Friday install", parsed.FinalTitle);
        Assert.Equal("Spoke with Jane", parsed.Note);
        Assert.Contains("Friday", parsed.Summary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParseAgentComplete_rejects_transcript_title()
    {
        var parsed = CaptureClarify.TryParseAgentComplete(
            """
            DONE
            TITLE: Suggested: Order MetroFiber
            Who is the contact?
            Reply below — Enter to send. Done when finished.
            User: Jane
            """);
        Assert.Null(parsed);
    }

    [Fact]
    public void Finalize_without_replies_uses_rewrite()
    {
        var result = CaptureClarify.Finalize("order metrofiber service", "Harbor Court", []);
        Assert.True(result.IsComplete);
        Assert.Equal("Order Metrofiber Service", result.FinalTitle);
        Assert.Null(result.Note);
        Assert.Null(result.Summary);
    }

    [Fact]
    public void Explicit_title_prefix_wins()
    {
        var title = CaptureClarify.ComposeFinalTitle(
            "order metrofiber",
            ["title: Schedule MetroFiber install"]);
        Assert.Equal("Schedule Metrofiber Install", title);
    }
}
