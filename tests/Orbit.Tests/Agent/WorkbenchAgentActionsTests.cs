using Orbit.Core.Agent;

namespace Orbit.Tests.Agent;

public sealed class WorkbenchAgentActionsTests
{
    [Fact]
    public void Parse_link_token_reads_direction_and_expectation()
    {
        var ok = WorkbenchAgentActions.TryParseReply(
            """
            These two are contingent — linking them.
            ORBIT_LINK_TASK
            DIRECTION: waits_for
            TASK: Confirm phone line count with vendor
            EXPECTS: line count
            """,
            out var mutation,
            out var display);

        Assert.True(ok);
        Assert.NotNull(mutation);
        Assert.True(mutation!.HasLinkRequest);
        Assert.Equal("waits_for", mutation.LinkDirection);
        Assert.Equal("Confirm phone line count with vendor", mutation.LinkTaskQuery);
        Assert.Equal("line count", mutation.LinkExpects);
        Assert.DoesNotContain("ORBIT_LINK_TASK", display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DIRECTION:", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("contingent", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_link_token_normalizes_direction_aliases()
    {
        Assert.True(WorkbenchAgentActions.TryParseReply(
            "ORBIT_LINK_TASK\nDIRECTION: blocks\nTASK: Open phone lines",
            out var mutation,
            out _));
        Assert.Equal("feeds", mutation!.LinkDirection);
    }

    [Fact]
    public void Link_target_resolution_requires_an_unambiguous_match()
    {
        var candidates = new[]
        {
            ("t1", "Confirm phone line count with vendor"),
            ("t2", "Open phone lines with carrier"),
            ("t3", "Order phone handsets"),
        };

        Assert.True(WorkbenchAgentActions.TryResolveLinkTarget("Open phone lines with carrier", candidates, out var exact));
        Assert.Equal("t2", exact);

        Assert.True(WorkbenchAgentActions.TryResolveLinkTarget("vendor", candidates, out var partial));
        Assert.Equal("t1", partial);

        // "phone" matches all three, so refuse rather than guess.
        Assert.False(WorkbenchAgentActions.TryResolveLinkTarget("phone", candidates, out _));
        Assert.False(WorkbenchAgentActions.TryResolveLinkTarget("nonexistent task", candidates, out _));
    }

    [Fact]
    public void Parse_update_token_with_title_and_status()
    {
        var ok = WorkbenchAgentActions.TryParseReply(
            """
            Updated the line.
            ORBIT_UPDATE_TASK
            TITLE: Confirm Pyrocomm phone-line count
            STATUS: active
            NEXT: Call Pyrocomm
            """,
            out var mutation,
            out var display);

        Assert.True(ok);
        Assert.NotNull(mutation);
        Assert.Equal("Confirm Pyrocomm phone-line count", mutation!.Title);
        Assert.Equal("active", mutation.Status);
        Assert.Equal("Call Pyrocomm", mutation.NextAction);
        Assert.DoesNotContain("ORBIT_UPDATE_TASK", display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TITLE:", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Updated", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_apply_that_uses_last_agent_proposal()
    {
        var ok = WorkbenchAgentActions.TryResolveApplyTitle(
            "Apply that as the title",
            "Confirm phone line count and requirements with Pyrocomm for MetroFiber setup",
            out var title);

        Assert.True(ok);
        Assert.Contains("Pyrocomm", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("?", title);
    }

    [Fact]
    public void Resolve_explicit_title_phrase()
    {
        var ok = WorkbenchAgentActions.TryResolveApplyTitle(
            "Set the title to: Wire up MetroFiber lines",
            "some other proposal",
            out var title);

        Assert.True(ok);
        Assert.Equal("Wire up MetroFiber lines", title);
    }

    [Fact]
    public void Resolve_apply_walks_back_past_clarifying_question()
    {
        var ok = WorkbenchAgentActions.TryResolveApplyTitle(
            "I am asking you to set the title on this project. Can you do it?",
            [
                "Yes. What title would you like to set for the Harbor Court project?",
                "Connect with Pyrocomm for MetroFiber setup phone-line count and requirements",
            ],
            out var title);

        Assert.True(ok);
        Assert.Contains("Pyrocomm", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Status_request_detects_blocked()
    {
        Assert.True(WorkbenchAgentActions.LooksLikeStatusUpdateRequest("mark this as blocked", out var status));
        Assert.Equal("blocked", status);
    }
}
