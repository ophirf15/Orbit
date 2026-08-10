using Orbit.Core.Preview;

namespace Orbit.Tests.Preview;

public sealed class MarkdownPreviewHtmlTests
{
    [Fact]
    public void FromMarkdown_IncludesHeadingHtml()
    {
        var html = MarkdownPreviewHtml.FromMarkdown("# Hello\n\nWorld", dark: true);
        Assert.Contains("<h1", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", html, StringComparison.Ordinal);
        Assert.Contains("#1c1c1c", html, StringComparison.Ordinal);
    }
}
