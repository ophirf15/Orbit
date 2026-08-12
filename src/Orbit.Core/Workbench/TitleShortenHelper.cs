namespace Orbit.Core.Workbench;

/// <summary>
/// Heuristic short title handles + non-destructive brief preservation.
/// Never silently overwrites — callers must confirm before applying.
/// </summary>
public static class TitleShortenHelper
{
    public const int DefaultMaxLength = 48;

    public const int MinUsefulLength = 8;

    /// <summary>
    /// Proposes a shorter title, or null when the current title is already short / no useful change.
    /// </summary>
    public static string? Suggest(string? title, int maxLength = DefaultMaxLength)
    {
        if (maxLength < MinUsefulLength)
        {
            maxLength = MinUsefulLength;
        }

        var original = NormalizeWhitespace(title);
        if (original.Length == 0 || original.Length <= maxLength)
        {
            return null;
        }

        var working = StripMailPrefixes(original);
        working = TakeFirstClause(working);
        working = TruncateAtWord(working, maxLength);
        working = NormalizeWhitespace(working);

        if (working.Length < MinUsefulLength)
        {
            working = TruncateAtWord(StripMailPrefixes(original), maxLength);
            working = NormalizeWhitespace(working);
        }

        if (working.Length == 0
            || string.Equals(working, original, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return working;
    }

    /// <summary>
    /// Builds the brief body that keeps the prior long title when accepting a shorter handle.
    /// Returns the existing body unchanged when the prior title is already present.
    /// </summary>
    public static string PreserveTitleInBrief(string priorTitle, string? currentBody)
    {
        var prior = NormalizeWhitespace(priorTitle);
        var body = (currentBody ?? string.Empty).Trim();
        if (prior.Length == 0)
        {
            return body;
        }

        if (body.Length == 0)
        {
            return prior;
        }

        if (body.Contains(prior, StringComparison.OrdinalIgnoreCase))
        {
            return body;
        }

        return prior + Environment.NewLine + Environment.NewLine + body;
    }

    /// <summary>
    /// Applies an accepted short title: returns the new title and the body to persist
    /// (body is null when no brief update is needed).
    /// </summary>
    public static (string Title, string? Body) ApplyAccepted(
        string currentTitle,
        string? currentBody,
        string acceptedShortTitle)
    {
        var current = NormalizeWhitespace(currentTitle);
        var accepted = NormalizeWhitespace(acceptedShortTitle);
        if (accepted.Length == 0)
        {
            return (current, null);
        }

        if (string.Equals(accepted, current, StringComparison.Ordinal))
        {
            return (current, null);
        }

        // Shorten or rewrite: keep long prior wording in brief when it would otherwise vanish.
        if (current.Length > accepted.Length
            || !current.Contains(accepted, StringComparison.OrdinalIgnoreCase))
        {
            var preserved = PreserveTitleInBrief(current, currentBody);
            var bodyUnchanged = string.Equals(
                preserved,
                (currentBody ?? string.Empty).Trim(),
                StringComparison.Ordinal);
            return (accepted, bodyUnchanged ? null : preserved);
        }

        return (accepted, null);
    }

    private static string StripMailPrefixes(string title)
    {
        var t = title;
        while (true)
        {
            if (t.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Fw:", StringComparison.OrdinalIgnoreCase))
            {
                t = t[3..].TrimStart();
                continue;
            }

            if (t.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase))
            {
                t = t[4..].TrimStart();
                continue;
            }

            break;
        }

        return t;
    }

    private static string TakeFirstClause(string title)
    {
        ReadOnlySpan<string> separators =
        [
            " — ",
            " – ",
            " - ",
            " · ",
            ": ",
            ". ",
            "; ",
        ];

        var cut = title.Length;
        foreach (var sep in separators)
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx >= MinUsefulLength && idx < cut)
            {
                cut = idx;
            }
        }

        return cut < title.Length ? title[..cut].Trim() : title;
    }

    private static string TruncateAtWord(string title, int maxLength)
    {
        if (title.Length <= maxLength)
        {
            return title;
        }

        var slice = title[..maxLength].TrimEnd();
        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace >= MinUsefulLength)
        {
            slice = slice[..lastSpace].TrimEnd();
        }

        return slice;
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
