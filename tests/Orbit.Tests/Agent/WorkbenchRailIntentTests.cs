using Orbit.Core.Agent;

namespace Orbit.Tests.Agent;

public sealed class WorkbenchRailIntentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_Empty_IsEmpty(string? text)
    {
        var result = WorkbenchRailIntent.Classify(text);
        Assert.Equal(WorkbenchRailIntent.Kind.Empty, result.Kind);
        Assert.Equal(string.Empty, result.Payload);
    }

    [Theory]
    [InlineData("? what's blocking", "what's blocking")]
    [InlineData("?what's blocking", "what's blocking")]
    [InlineData("ask what's blocking", "what's blocking")]
    [InlineData("ASK status of Harbor", "status of Harbor")]
    [InlineData("hermes summarize today", "summarize today")]
    [InlineData("what's blocking?", "what's blocking")]
    public void Classify_AskMarkers_AreHermes(string text, string expected)
    {
        var result = WorkbenchRailIntent.Classify(text);
        Assert.Equal(WorkbenchRailIntent.Kind.AskHermes, result.Kind);
        Assert.Equal(expected, result.Payload);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("ask ")]
    [InlineData("hermes ")]
    [InlineData("?   ")]
    public void Classify_AskMarkersWithoutContent_FallThroughToCaptureOrEmpty(string text)
    {
        var result = WorkbenchRailIntent.Classify(text);
        // Marker alone is not a Hermes ask; lone "?" becomes capture of "?" after trim rules,
        // empty ask/hermes prefixes become Capture of the raw trimmed marker text or Empty.
        Assert.NotEqual(WorkbenchRailIntent.Kind.AskHermes, result.Kind);
    }

    [Theory]
    [InlineData("new project Foo", "Foo")]
    [InlineData("new project \"Harbor Court\"", "Harbor Court")]
    [InlineData("start new project Acme", "Acme")]
    [InlineData("NEW PROJECT", "Untitled project")]
    public void Classify_NewProject_CreatesProjectCommand(string text, string expectedName)
    {
        var result = WorkbenchRailIntent.Classify(text);
        Assert.Equal(WorkbenchRailIntent.Kind.NewProject, result.Kind);
        Assert.Equal(expectedName, result.Payload);
    }

    [Theory]
    [InlineData("Follow up vendor Friday")]
    [InlineData("order metrofiber")]
    [InlineData("Call Jane about bond")]
    public void Classify_PlainActionableText_IsCapture(string text)
    {
        var result = WorkbenchRailIntent.Classify(text);
        Assert.Equal(WorkbenchRailIntent.Kind.Capture, result.Kind);
        Assert.Equal(text, result.Payload);
    }

    [Fact]
    public void Classify_AmbiguousQuestionWithoutMarker_PrefersCapture()
    {
        // Safety: no leading ?, ask, hermes, or trailing ? → confirm via capture, not Hermes mutate.
        var result = WorkbenchRailIntent.Classify("what is blocking Harbor Court");
        Assert.Equal(WorkbenchRailIntent.Kind.Capture, result.Kind);
        Assert.Equal("what is blocking Harbor Court", result.Payload);
    }

    [Fact]
    public void Classify_NewProjectBeatsTrailingQuestion()
    {
        var result = WorkbenchRailIntent.Classify("new project Foo?");
        Assert.Equal(WorkbenchRailIntent.Kind.NewProject, result.Kind);
        Assert.Equal("Foo", result.Payload);
    }
}
