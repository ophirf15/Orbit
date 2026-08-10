using Markdig;

namespace Orbit.Core.Preview;

public static class MarkdownPreviewHtml
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static string FromMarkdown(string markdown, bool dark)
    {
        var body = Markdown.ToHtml(markdown ?? string.Empty, Pipeline);
        var bg = dark ? "#1c1c1c" : "#f3f3f3";
        var fg = dark ? "#f3f3f3" : "#1a1a1a";
        var muted = dark ? "#b3b3b3" : "#5c5c5c";
        var codeBg = dark ? "#2d2d2d" : "#ececec";
        var border = dark ? "#3d3d3d" : "#d0d0d0";

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
              <meta charset="utf-8" />
              <style>
                body { font-family: "Segoe UI", sans-serif; margin: 16px; background: {{bg}}; color: {{fg}}; line-height: 1.45; }
                a { color: #4cc2ff; }
                code, pre { font-family: Consolas, "Cascadia Mono", monospace; background: {{codeBg}}; }
                pre { padding: 12px; overflow: auto; border-radius: 6px; border: 1px solid {{border}}; }
                code { padding: 1px 4px; border-radius: 4px; }
                pre code { padding: 0; background: transparent; }
                blockquote { border-left: 3px solid {{border}}; margin-left: 0; padding-left: 12px; color: {{muted}}; }
                table { border-collapse: collapse; width: 100%; }
                th, td { border: 1px solid {{border}}; padding: 6px 8px; }
                img { max-width: 100%; height: auto; }
                h1, h2, h3 { line-height: 1.25; }
              </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }
}
