using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class CapturePreviewProposerTests
{
    [Fact]
    public void Propose_Empty_ReturnsBlankEditablePreview()
    {
        var p = CapturePreviewProposer.Propose("   ");

        Assert.Equal(string.Empty, p.Title.Trim());
        Assert.Null(p.Brief);
        Assert.Null(p.NextAction);
        Assert.Null(p.WaitingOnHint);
        Assert.Equal(CapturePreviewProposer.SourceCapture, p.Source);
    }

    [Fact]
    public void Propose_PreservesOriginalVerbatim()
    {
        const string raw = "  Call Grant about PMA — waiting on signed copy by tomorrow  ";
        var p = CapturePreviewProposer.Propose(raw);

        Assert.Equal(raw, p.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(p.Title));
        Assert.DoesNotContain("  ", p.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void Propose_LongNote_ShortTitleAndBriefKeepsOriginal()
    {
        var raw =
            "Need to follow up with MetroFiber on the permit package bond paperwork " +
            "and confirm the inspection window for next week at Unit 4B";
        var p = CapturePreviewProposer.Propose(raw);

        Assert.True(p.Title.Length <= CapturePreviewProposer.MaxTitleLength);
        Assert.Equal(raw, p.Brief);
        Assert.Equal("Unit 4B", p.LocationHint);
    }

    [Fact]
    public void Propose_WaitingOn_ExtractsHintAndNext()
    {
        var p = CapturePreviewProposer.Propose("Grant PMA — waiting on Grant to return signed PMA");

        Assert.Contains("Grant", p.WaitingOnHint, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Follow up on", p.NextAction, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Grant", p.PeopleHint);
    }

    [Fact]
    public void Propose_RelativeDue_Tomorrow()
    {
        var p = CapturePreviewProposer.Propose("Send bond docs due tomorrow");

        Assert.False(string.IsNullOrWhiteSpace(p.DueHint));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", p.DueHint!);
    }

    [Fact]
    public void Propose_NoSignal_SkipsPeopleAndLocation()
    {
        var p = CapturePreviewProposer.Propose("Order more printer paper");

        Assert.Null(p.PeopleHint);
        Assert.Null(p.LocationHint);
        Assert.Null(p.WaitingOnHint);
    }

    [Fact]
    public void BuildPersistBrief_KeepsOriginalWhenTitleCleaned()
    {
        const string original = "Call vendor about the long-running permit bond issue at the site";
        var title = CapturePreviewProposer.ProposeTitle(original);
        var brief = CapturePreviewProposer.BuildPersistBrief(
            original,
            title,
            proposedBrief: null,
            peopleHint: "Acme",
            waitingOnHint: "bond paperwork");

        Assert.NotNull(brief);
        Assert.Contains(original, brief!, StringComparison.Ordinal);
        Assert.Contains("People: Acme", brief!, StringComparison.Ordinal);
        Assert.Contains("Waiting on: bond paperwork", brief!, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPersistBrief_DoesNotDuplicatePeopleAlreadyInBrief()
    {
        const string original = "Ask Grant for the signed PMA";
        var brief = CapturePreviewProposer.BuildPersistBrief(
            original,
            "Ask Grant for PMA",
            proposedBrief: original,
            peopleHint: "Grant");

        Assert.Equal(original, brief);
        Assert.DoesNotContain("People:", brief, StringComparison.Ordinal);
    }
}

public sealed class CaptureMatchReasonFormatterTests
{
    [Theory]
    [InlineData("alias", "Matched via alias")]
    [InlineData("exact_alias", "Matched via alias")]
    [InlineData("name", "Matched via project name")]
    [InlineData("code", "Matched via project code")]
    [InlineData("name_token", "Matched via name token")]
    [InlineData("folder", "Matched via folder path")]
    [InlineData("address", "Matched via project address")]
    [InlineData("contact", "Matched via project contact")]
    [InlineData("scoped", "Scoped project")]
    [InlineData("operator", "Selected by you")]
    [InlineData("no_match", "No automatic match")]
    public void Format_KnownCodes(string code, string expected)
    {
        Assert.Equal(expected, CaptureMatchReasonFormatter.Format(code));
    }

    [Fact]
    public void Format_UnknownCode_UsesMatchedViaPrefix()
    {
        Assert.Equal("Matched via thread", CaptureMatchReasonFormatter.Format("thread"));
    }

    [Fact]
    public void FormatCaption_IncludesConfidenceWhenPresent()
    {
        var caption = CaptureMatchReasonFormatter.FormatCaption("Widget", "alias", 0.88);
        Assert.Contains("Matched via alias", caption, StringComparison.Ordinal);
        Assert.Contains("88%", caption, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_Blank_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CaptureMatchReasonFormatter.Format(null));
        Assert.Equal(string.Empty, CaptureMatchReasonFormatter.Format("  "));
    }
}
