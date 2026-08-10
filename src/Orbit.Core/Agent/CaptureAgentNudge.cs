namespace Orbit.Core.Agent;

/// <summary>Local (offline) 2–4 line nudges after a workbench capture.</summary>
public static class CaptureAgentNudge
{
    public static IReadOnlyList<string> BuildLocal(string captureText, string? projectName)
    {
        var text = (captureText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return [];
        }

        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        var lines = new List<string>(4);

        var rewrite = SuggestRewrite(text);
        if (!string.IsNullOrWhiteSpace(rewrite) && !string.Equals(rewrite, text, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Reword? {rewrite}");
        }
        else
        {
            lines.Add($"Captured on {project}. Want a tighter title?");
        }

        lines.Add(PickClarifyingQuestion(text, project));
        lines.Add("Need context — files, contact, or related email?");

        if (LooksLikeAction(text))
        {
            lines.Add("Status: New · set Active when you start, Waiting if blocked on someone.");
        }

        return lines.Take(4).ToList();
    }

    public static string Format(IEnumerable<string> lines) =>
        string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

    public static string BuildTaskSummaryLocal(
        string projectName,
        string taskTitle,
        string status,
        IReadOnlyList<string> notes)
    {
        var statusLabel = status switch
        {
            "blocked" => "Blocked",
            "waiting" => "Waiting",
            "active" => "Active",
            "not_started" => "New",
            _ => status,
        };

        var noteHint = notes.Count == 0
            ? "No notes yet — capture context on this task."
            : $"Latest note: {Truncate(notes[0], 120)}";

        return $"{taskTitle} on {projectName} is {statusLabel}. {noteHint}";
    }

    private static string SuggestRewrite(string text)
    {
        var trimmed = text.Trim().TrimEnd('.', '!', '?');
        if (trimmed.Length == 0)
        {
            return text;
        }

        if (trimmed.Length > 72 || trimmed.Contains('\n'))
        {
            return trimmed;
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

    private static bool LooksLikeAction(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.StartsWith("order", StringComparison.Ordinal)
               || lower.StartsWith("fix", StringComparison.Ordinal)
               || lower.StartsWith("call", StringComparison.Ordinal)
               || lower.StartsWith("email", StringComparison.Ordinal)
               || lower.StartsWith("set up", StringComparison.Ordinal)
               || lower.StartsWith("setup", StringComparison.Ordinal)
               || lower.Contains(" follow", StringComparison.Ordinal)
               || lower.Contains("need ", StringComparison.Ordinal);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
