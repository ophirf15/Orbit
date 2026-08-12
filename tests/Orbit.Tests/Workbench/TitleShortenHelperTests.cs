using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class TitleShortenHelperTests
{
    [Fact]
    public void Suggest_AlreadyShort_ReturnsNull()
    {
        Assert.Null(TitleShortenHelper.Suggest("Call vendor"));
    }

    [Fact]
    public void Suggest_StripsReAndTakesFirstClause()
    {
        var suggestion = TitleShortenHelper.Suggest(
            "Re: Follow up with MetroFiber about permit package and bond paperwork — next week");

        Assert.NotNull(suggestion);
        Assert.DoesNotContain("Re:", suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.True(suggestion!.Length <= TitleShortenHelper.DefaultMaxLength);
        Assert.Contains("Follow up with MetroFiber", suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Suggest_TruncatesLongRunOnTitle()
    {
        var longTitle =
            "Coordinate the full architectural review package including structural notes electrical " +
            "and plumbing markups before Friday's filing deadline with the city";
        var suggestion = TitleShortenHelper.Suggest(longTitle);

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.Length <= TitleShortenHelper.DefaultMaxLength);
        Assert.False(string.Equals(suggestion, longTitle, StringComparison.Ordinal));
    }

    [Fact]
    public void PreserveTitleInBrief_PrependsWhenMissing()
    {
        var body = TitleShortenHelper.PreserveTitleInBrief(
            "Long original title about permits",
            "Waiting on bond paperwork.");

        Assert.StartsWith("Long original title about permits", body, StringComparison.Ordinal);
        Assert.Contains("Waiting on bond paperwork.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PreserveTitleInBrief_DoesNotDuplicate()
    {
        var prior = "Long original title about permits";
        var existing = prior + "\n\nWaiting on bond.";
        var body = TitleShortenHelper.PreserveTitleInBrief(prior, existing);

        Assert.Equal(existing, body);
    }

    [Fact]
    public void ApplyAccepted_PreservesBriefAndShortensTitle()
    {
        var (title, body) = TitleShortenHelper.ApplyAccepted(
            "Re: Follow up with MetroFiber about permit package and bond paperwork",
            "Owner wants Friday filing.",
            "MetroFiber permit follow-up");

        Assert.Equal("MetroFiber permit follow-up", title);
        Assert.NotNull(body);
        Assert.Contains("Re: Follow up with MetroFiber", body!, StringComparison.Ordinal);
        Assert.Contains("Owner wants Friday filing.", body!, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyAccepted_CancelPath_EmptyAcceptedKeepsCurrent()
    {
        var (title, body) = TitleShortenHelper.ApplyAccepted(
            "Keep me",
            "Brief stays",
            "   ");

        Assert.Equal("Keep me", title);
        Assert.Null(body);
    }
}
