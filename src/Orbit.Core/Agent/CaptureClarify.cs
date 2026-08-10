namespace Orbit.Core.Agent;

public sealed class CaptureClarifyResult
{
    public required string Message { get; init; }

    public bool IsComplete { get; init; }

    public string? FinalTitle { get; init; }

    /// <summary>Short subtitle / next-action line derived from the clarify chat.</summary>
    public string? Note { get; init; }

    /// <summary>Longer task summary from Q&amp;A (optional; stored in task body).</summary>
    public string? Summary { get; init; }

    public static CaptureClarifyResult Incomplete(string message) => new()
    {
        Message = message.Trim(),
        IsComplete = false,
    };

    public static CaptureClarifyResult Complete(
        string finalTitle,
        string message,
        string? note = null,
        string? summary = null) => new()
    {
        Message = message.Trim(),
        IsComplete = true,
        FinalTitle = CaptureClarify.SanitizeTitle(finalTitle),
        Note = string.IsNullOrWhiteSpace(note)
            ? null
            : CaptureClarify.TruncatePublic(note.Trim(), 160),
        Summary = string.IsNullOrWhiteSpace(summary) ? null : summary.Trim(),
    };
}

/// <summary>Local clarify loop after a workbench capture — ask, reply, commit refined title + summary.</summary>
public static class CaptureClarify
{
    public const int MaxUserReplies = 3;
    public const int MaxTitleLength = 80;

    public static CaptureClarifyResult Open(string captureText, string? projectName)
    {
        var text = (captureText ?? string.Empty).Trim();
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        if (text.Length == 0)
        {
            return CaptureClarifyResult.Incomplete("What should this line be?");
        }

        var rewrite = SuggestRewrite(text);
        var question = PickClarifyingQuestion(text, project);
        var lines = new List<string>();
        if (!string.Equals(rewrite, text, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Suggested: {rewrite}");
        }

        lines.Add(question);
        lines.Add("Reply below — Enter to send. Done when finished.");
        return CaptureClarifyResult.Incomplete(string.Join("\n", lines));
    }

    public static CaptureClarifyResult Continue(
        string originalCapture,
        string? projectName,
        IReadOnlyList<string> userReplies,
        string latestReply)
    {
        var replies = userReplies.Concat([latestReply.Trim()])
            .Where(r => r.Length > 0)
            .ToList();

        if (replies.Count >= MaxUserReplies)
        {
            return Finalize(originalCapture, projectName, replies);
        }

        // One follow-up if the reply is very short / vague; otherwise finalize.
        if (LooksVague(latestReply) && replies.Count == 1)
        {
            return CaptureClarifyResult.Incomplete(
                "Got it — anything else to pin (owner, deadline, or blocker)? Or hit Done.");
        }

        return Finalize(originalCapture, projectName, replies);
    }

    public static CaptureClarifyResult Finalize(
        string originalCapture,
        string? projectName,
        IReadOnlyList<string> userReplies)
    {
        var title = ComposeFinalTitle(originalCapture, userReplies);
        var note = ComposeSubtitle(userReplies);
        var summary = ComposeSummary(originalCapture, userReplies);
        return CaptureClarifyResult.Complete(
            title,
            $"Locked in:\n{title}",
            note,
            summary);
    }

    public static CaptureClarifyResult? TryParseAgentComplete(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.Contains("DONE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? title = null;
        string? note = null;
        string? summary = null;
        foreach (var line in normalized.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
            {
                title = line["TITLE:".Length..].Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("SUBTITLE:", StringComparison.OrdinalIgnoreCase))
            {
                var prefix = line.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase) ? "NOTE:" : "SUBTITLE:";
                note = line[prefix.Length..].Trim();
            }
            else if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                summary = line["SUMMARY:".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Reject transcript dumps masquerading as titles.
        if (LooksLikeTranscriptDump(title))
        {
            return null;
        }

        return CaptureClarifyResult.Complete(title, $"Locked in:\n{SanitizeTitle(title)}", note, summary);
    }

    /// <summary>
    /// Short actionable title only. Never concatenates the clarify chat into the title.
    /// Replies feed <see cref="ComposeSubtitle"/> / <see cref="ComposeSummary"/>.
    /// </summary>
    public static string ComposeFinalTitle(string originalCapture, IReadOnlyList<string> userReplies)
    {
        var original = (originalCapture ?? string.Empty).Trim();

        // Prefer an explicit title rewrite from the user.
        foreach (var reply in userReplies.Reverse())
        {
            var r = reply.Trim().Trim('"');
            if (r.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                return SanitizeTitle(SuggestRewrite(r["title:".Length..].Trim()));
            }
        }

        // If the last reply is itself a short restated title (not Q&A detail), use it.
        if (userReplies.Count > 0)
        {
            var last = userReplies[^1].Trim();
            if (last.Length is >= 12 and <= MaxTitleLength
                && !last.Contains('\n')
                && !last.EndsWith('?')
                && LooksLikeTitleCandidate(last, original))
            {
                return SanitizeTitle(SuggestRewrite(last));
            }
        }

        return SanitizeTitle(SuggestRewrite(original));
    }

    public static string? ComposeSubtitle(IReadOnlyList<string> userReplies)
    {
        var bits = userReplies
            .Select(r => r.Trim())
            .Where(r => r.Length > 0 && !r.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            .Where(r => !LooksVague(r))
            .Select(r => Truncate(CollapseWhitespace(r), 72))
            .ToList();
        if (bits.Count == 0)
        {
            return null;
        }

        return Truncate(string.Join(" · ", bits), 160);
    }

    public static string? ComposeSummary(string originalCapture, IReadOnlyList<string> userReplies)
    {
        var replies = userReplies
            .Select(r => r.Trim())
            .Where(r => r.Length > 0 && !r.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (replies.Count == 0)
        {
            return null;
        }

        var lines = new List<string>
        {
            $"Capture: {CollapseWhitespace(originalCapture.Trim())}",
        };
        for (var i = 0; i < replies.Count; i++)
        {
            lines.Add($"Clarified ({i + 1}): {CollapseWhitespace(replies[i])}");
        }

        return string.Join("\n", lines);
    }

    public static string SanitizeTitle(string title)
    {
        var t = CollapseWhitespace(title ?? string.Empty).Trim().Trim('"', '\'');
        if (t.Length == 0)
        {
            return "Untitled task";
        }

        // Drop accidental protocol lines if the model jammed them into TITLE.
        if (t.StartsWith("DONE", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("NOTE:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
        {
            t = t.Contains(':') ? t[(t.IndexOf(':') + 1)..].Trim() : t;
        }

        return Truncate(t, MaxTitleLength);
    }

    private static bool LooksLikeTranscriptDump(string title)
    {
        var t = title.Trim();
        if (t.Length > MaxTitleLength + 40)
        {
            return true;
        }

        if (t.Count(c => c == '\n') >= 2)
        {
            return true;
        }

        // Common chat markers that should never be a task title.
        if (t.Contains("Reply below", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Locked in:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Suggested:", StringComparison.OrdinalIgnoreCase)
            || t.Contains("Suggested:", StringComparison.OrdinalIgnoreCase)
               && t.Contains('?'))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeTitleCandidate(string last, string original)
    {
        // Prefer short imperative restatements that share intent with the capture,
        // not long answer blobs that belong in the summary.
        if (last.Contains(" — ") || last.Contains(" · "))
        {
            return false;
        }

        var words = last.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 12)
        {
            return false;
        }

        return ContainsSharedToken(last, original);
    }

    private static bool ContainsSharedToken(string a, string b)
    {
        var tokensA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '-', '—').ToLowerInvariant())
            .Where(t => t.Length > 3)
            .ToHashSet(StringComparer.Ordinal);
        return b.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '-', '—').ToLowerInvariant())
            .Where(t => t.Length > 3)
            .Any(tokensA.Contains);
    }

    private static bool LooksVague(string reply)
    {
        var t = reply.Trim();
        return t.Length < 12
               || t.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || t.Equals("no", StringComparison.OrdinalIgnoreCase)
               || t.Equals("ok", StringComparison.OrdinalIgnoreCase)
               || t.Equals("sure", StringComparison.OrdinalIgnoreCase);
    }

    private static string SuggestRewrite(string text)
    {
        var trimmed = CollapseWhitespace(text.Trim().TrimEnd('.', '!', '?'));
        if (trimmed.Length == 0)
        {
            return text.Trim();
        }

        if (trimmed.Length > MaxTitleLength)
        {
            return Truncate(trimmed, MaxTitleLength);
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return trimmed;
        }

        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0)
            {
                continue;
            }

            if (i > 0 && IsMinorWord(w))
            {
                words[i] = w.ToLowerInvariant();
                continue;
            }

            words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    private static bool IsMinorWord(string w) =>
        w.Equals("a", StringComparison.OrdinalIgnoreCase)
        || w.Equals("an", StringComparison.OrdinalIgnoreCase)
        || w.Equals("the", StringComparison.OrdinalIgnoreCase)
        || w.Equals("and", StringComparison.OrdinalIgnoreCase)
        || w.Equals("or", StringComparison.OrdinalIgnoreCase)
        || w.Equals("of", StringComparison.OrdinalIgnoreCase)
        || w.Equals("to", StringComparison.OrdinalIgnoreCase)
        || w.Equals("for", StringComparison.OrdinalIgnoreCase)
        || w.Equals("on", StringComparison.OrdinalIgnoreCase)
        || w.Equals("in", StringComparison.OrdinalIgnoreCase);

    private static string PickClarifyingQuestion(string text, string project)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("email") || lower.Contains("mail"))
        {
            return "Who should that email go to, and what's the ask?";
        }

        if (lower.Contains("call") || lower.Contains("meet"))
        {
            return "When is that, and who else needs to be there?";
        }

        if (lower.Contains("order") || lower.Contains("buy") || lower.Contains("pay"))
        {
            return "Any account #, vendor contact, or deadline?";
        }

        if (lower.Contains("fix") || lower.Contains("bug") || lower.Contains("broken"))
        {
            return "What's broken vs expected — and where (file/system)?";
        }

        if (lower.Contains("follow") || lower.Contains("check"))
        {
            return "What's the outcome you want from the follow-up?";
        }

        return $"What's the next concrete action on {project}?";
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var parts = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    internal static string TruncatePublic(string value, int max) => Truncate(value, max);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
