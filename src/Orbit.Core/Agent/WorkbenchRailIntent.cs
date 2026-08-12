using System.Text.RegularExpressions;

namespace Orbit.Core.Agent;

/// <summary>
/// Routes bottom workbench rail text to ask / capture / command without a mode picker.
/// Prefers confirm (capture dialog) over silent mutation for ambiguous plain text.
/// </summary>
public static class WorkbenchRailIntent
{
    public enum Kind
    {
        Empty,
        AskHermes,
        NewProject,
        Capture,
    }

    public readonly record struct Result(Kind Kind, string Payload);

    private static readonly Regex NewProjectRegex = new(
        @"^(?:start\s+)?new\s+project(?:\s+[""']?(.+?)[""']?)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Classify trimmed rail input.
    /// Explicit ask markers (<c>?</c>, <c>ask </c>, <c>hermes </c>) and trailing <c>?</c> → Hermes.
    /// <c>new project …</c> → create project. Everything else actionable → capture (confirm).
    /// </summary>
    public static Result Classify(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return new Result(Kind.Empty, string.Empty);
        }

        // Prefer new-project even when the operator typed a trailing '?'.
        var projectCandidate = trimmed.EndsWith('?')
            ? trimmed.TrimEnd('?', ' ', '\t').Trim()
            : trimmed;
        if (projectCandidate.Length > 0
            && TryParseNewProject(projectCandidate, out var projectName))
        {
            return new Result(Kind.NewProject, projectName);
        }

        if (TryParseAskHermes(trimmed, out var askText))
        {
            return new Result(Kind.AskHermes, askText);
        }

        // Plain actionable text → capture dialog (confirm), never silent blank create.
        return new Result(Kind.Capture, trimmed);
    }

    public static bool TryParseAskHermes(string text, out string askText)
    {
        askText = text;
        if (text.StartsWith('?'))
        {
            askText = text[1..].Trim();
            return askText.Length > 0;
        }

        if (text.StartsWith("ask ", StringComparison.OrdinalIgnoreCase))
        {
            askText = text[4..].Trim();
            return askText.Length > 0;
        }

        if (text.StartsWith("hermes ", StringComparison.OrdinalIgnoreCase))
        {
            askText = text[7..].Trim();
            return askText.Length > 0;
        }

        // Clear question signal without a leading marker — still Hermes, not silent mutate.
        if (text.EndsWith('?') && text.Length > 1)
        {
            askText = text.TrimEnd('?', ' ', '\t').Trim();
            return askText.Length > 0;
        }

        return false;
    }

    public static bool TryParseNewProject(string text, out string name)
    {
        name = string.Empty;
        var match = NewProjectRegex.Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        name = match.Groups[1].Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : "Untitled project";
        return true;
    }
}
