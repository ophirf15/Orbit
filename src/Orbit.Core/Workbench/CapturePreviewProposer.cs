using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbit.Core.Workbench;

/// <summary>
/// Structured capture preview before save. Original wording is always preserved separately;
/// title / brief / next are editable proposals only.
/// </summary>
public sealed record CapturePreviewProposal(
    string OriginalText,
    string Title,
    string? Brief,
    string? NextAction,
    string? DueHint,
    string? WaitingOnHint,
    string? PeopleHint,
    string? LocationHint,
    string Source = "capture");

/// <summary>
/// Formats <see cref="ProjectMatchCandidate"/> / capture match reason codes for UI captions.
/// </summary>
public static class CaptureMatchReasonFormatter
{
    /// <summary>Human label for a reason code (e.g. <c>alias</c> → <c>Matched via alias</c>).</summary>
    public static string Format(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode))
        {
            return string.Empty;
        }

        var code = reasonCode.Trim().ToLowerInvariant();
        return code switch
        {
            "name" or "exact_name" or "name_overlap" => "Matched via project name",
            "code" or "exact_code" => "Matched via project code",
            "alias" or "exact_alias" or "alias_overlap" => "Matched via alias",
            "name_token" => "Matched via name token",
            "folder" or "folder_path" => "Matched via folder path",
            "address" or "dossier_address" => "Matched via project address",
            "contact" or "dossier_contact" => "Matched via project contact",
            "default" or "scoped" => "Scoped project",
            "operator" or "explicit" => "Selected by you",
            "no_match" => "No automatic match",
            _ => $"Matched via {reasonCode.Trim()}",
        };
    }

    /// <summary>Caption under the project picker when a project is auto-matched or scoped.</summary>
    public static string FormatCaption(string? projectName, string? reasonCode, double? score = null)
    {
        var why = Format(reasonCode);
        if (why.Length == 0)
        {
            return string.Empty;
        }

        var name = string.IsNullOrWhiteSpace(projectName) ? null : projectName.Trim();
        var conf = score is { } s and > 0 and < 1.0001
            ? $" · {Math.Round(s * 100, MidpointRounding.AwayFromZero):0}%"
            : string.Empty;

        if (name is null
            || string.Equals(reasonCode, "default", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, "scoped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, "operator", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, "explicit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reasonCode, "no_match", StringComparison.OrdinalIgnoreCase))
        {
            return why + conf;
        }

        return $"{why}{conf}";
    }
}

/// <summary>
/// Pure heuristics: short note → editable title / brief / next / optional due / waiting / people / location.
/// Never invents fields without a text signal. Hermes enrichment can replace this later.
/// </summary>
public static partial class CapturePreviewProposer
{
    public const int MaxTitleLength = 80;

    public const string DefaultNextAction = "Define next move";

    public const string SourceCapture = "capture";

    /// <summary>Build a field proposal from raw capture text. Empty input → blank editable preview.</summary>
    public static CapturePreviewProposal Propose(string? rawText)
    {
        var original = rawText ?? string.Empty;
        var trimmed = original.Trim();
        if (trimmed.Length == 0)
        {
            return new CapturePreviewProposal(
                OriginalText: original,
                Title: string.Empty,
                Brief: null,
                NextAction: null,
                DueHint: null,
                WaitingOnHint: null,
                PeopleHint: null,
                LocationHint: null,
                Source: SourceCapture);
        }

        var title = ProposeTitle(trimmed);
        var brief = ProposeBrief(trimmed, title);
        var waiting = ProposeWaitingOn(trimmed);
        var people = ProposePeople(trimmed, waiting);
        var location = ProposeLocation(trimmed);
        var due = ProposeDue(trimmed);
        var next = ProposeNextAction(trimmed, waiting);

        return new CapturePreviewProposal(
            OriginalText: original,
            Title: title,
            Brief: brief,
            NextAction: next,
            DueHint: due,
            WaitingOnHint: waiting,
            PeopleHint: people,
            LocationHint: location,
            Source: SourceCapture);
    }

    /// <summary>
    /// Merge preview extras into a persistable brief without dropping original wording.
    /// </summary>
    public static string? BuildPersistBrief(
        string originalText,
        string title,
        string? proposedBrief,
        string? peopleHint = null,
        string? locationHint = null,
        string? waitingOnHint = null)
    {
        var original = (originalText ?? string.Empty).Trim();
        var cleanTitle = NormalizeWhitespace(title);
        var brief = string.IsNullOrWhiteSpace(proposedBrief)
            ? null
            : proposedBrief.Trim();

        // Prefer operator-edited brief; else keep original when title was cleaned.
        if (string.IsNullOrWhiteSpace(brief))
        {
            // Preserve original when title was cleaned/shortened (including case-only rewrites).
            if (original.Length > 0
                && !string.Equals(NormalizeWhitespace(original), cleanTitle, StringComparison.Ordinal))
            {
                brief = original;
            }
        }
        else if (original.Length > 0
                 && !brief.Contains(original, StringComparison.Ordinal)
                 && !string.Equals(NormalizeWhitespace(original), cleanTitle, StringComparison.Ordinal))
        {
            brief = original + Environment.NewLine + Environment.NewLine + brief;
        }

        var extras = new List<string>();
        AppendExtra(extras, brief, "People", peopleHint);
        AppendExtra(extras, brief, "Location", locationHint);
        AppendExtra(extras, brief, "Waiting on", waitingOnHint);

        if (extras.Count == 0)
        {
            return brief;
        }

        var block = string.Join(Environment.NewLine, extras);
        return string.IsNullOrWhiteSpace(brief) ? block : brief.TrimEnd() + Environment.NewLine + block;
    }

    public static string ProposeTitle(string trimmed)
    {
        var working = TakeFirstClause(CollapseWhitespace(trimmed));
        working = StripTrailingNoise(working);

        var shortened = TitleShortenHelper.Suggest(working, MaxTitleLength);
        if (!string.IsNullOrWhiteSpace(shortened))
        {
            working = shortened;
        }
        else if (working.Length > MaxTitleLength)
        {
            working = TruncateAtWord(working, MaxTitleLength);
        }

        return TitleCaseLight(working);
    }

    private static string? ProposeBrief(string trimmed, string title)
    {
        if (string.Equals(NormalizeWhitespace(trimmed), NormalizeWhitespace(title), StringComparison.OrdinalIgnoreCase))
        {
            // Short note that already is the title — no separate brief needed.
            return null;
        }

        // Preserve full original wording as the brief when title was cleaned/shortened.
        return trimmed;
    }

    private static string ProposeNextAction(string trimmed, string? waitingOn)
    {
        if (!string.IsNullOrWhiteSpace(waitingOn))
        {
            return $"Follow up on {waitingOn.Trim()}";
        }

        var followUp = FollowUpActionRegex().Match(trimmed);
        if (followUp.Success)
        {
            var action = CollapseWhitespace(followUp.Groups[1].Value).Trim().TrimEnd('.', '!', '?');
            if (action.Length >= 4)
            {
                return TitleCaseLight(TruncateAtWord(action, 120));
            }
        }

        var needTo = NeedToActionRegex().Match(trimmed);
        if (needTo.Success)
        {
            var action = CollapseWhitespace(needTo.Groups[1].Value).Trim().TrimEnd('.', '!', '?');
            if (action.Length >= 4)
            {
                return TitleCaseLight(TruncateAtWord(action, 120));
            }
        }

        // Imperative short notes can double as next move.
        if (trimmed.Length <= 72
            && !trimmed.Contains('\n')
            && LooksImperative(trimmed))
        {
            return TitleCaseLight(CollapseWhitespace(trimmed).TrimEnd('.', '!', '?'));
        }

        return DefaultNextAction;
    }

    private static string? ProposeWaitingOn(string trimmed)
    {
        var match = WaitingOnRegex().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var target = CollapseWhitespace(match.Groups[1].Value).Trim().TrimEnd('.', ',', ';', '!', '?');
        if (target.Length < 2 || target.Length > 120)
        {
            return null;
        }

        return target;
    }

    private static string? ProposePeople(string trimmed, string? waitingOn)
    {
        // Prefer an explicit person/vendor cue; otherwise reuse a waiting-on person-like target.
        var from = FromPersonRegex().Match(trimmed);
        if (from.Success)
        {
            var name = CollapseWhitespace(from.Groups[1].Value).Trim().TrimEnd('.', ',', ';');
            if (IsPersonLike(name))
            {
                return name;
            }
        }

        var ask = AskPersonRegex().Match(trimmed);
        if (ask.Success)
        {
            var name = CollapseWhitespace(ask.Groups[1].Value).Trim().TrimEnd('.', ',', ';');
            if (IsPersonLike(name))
            {
                return name;
            }
        }

        if (!string.IsNullOrWhiteSpace(waitingOn))
        {
            // "waiting on Grant" / "waiting on Grant to return…" → people = Grant
            if (IsPersonLike(waitingOn))
            {
                return waitingOn.Trim();
            }

            var first = waitingOn.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsPersonLike(first))
            {
                return first;
            }
        }

        return null;
    }

    private static string? ProposeLocation(string trimmed)
    {
        var unit = UnitRegex().Match(trimmed);
        if (unit.Success)
        {
            return CollapseWhitespace(unit.Value).Trim();
        }

        var suite = SuiteRegex().Match(trimmed);
        if (suite.Success)
        {
            return CollapseWhitespace(suite.Value).Trim();
        }

        return null;
    }

    private static string? ProposeDue(string trimmed)
    {
        var iso = IsoDateRegex().Match(trimmed);
        if (iso.Success && DateTime.TryParse(iso.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedIso))
        {
            return parsedIso.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var us = UsDateRegex().Match(trimmed);
        if (us.Success
            && DateTime.TryParse(us.Value, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var parsedUs))
        {
            return parsedUs.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        var relative = RelativeDueRegex().Match(trimmed);
        if (relative.Success)
        {
            var token = relative.Groups[1].Value.Trim().ToLowerInvariant();
            var today = DateTime.UtcNow.Date;
            var due = token switch
            {
                "today" => today,
                "tomorrow" => today.AddDays(1),
                _ => (DateTime?)null,
            };
            if (due is { } d)
            {
                return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        var byDay = ByWeekdayRegex().Match(trimmed);
        if (byDay.Success)
        {
            // Keep human weekday hint — App/editor can leave as free text; UpdateTask expects ISO when possible.
            return "by " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(byDay.Groups[1].Value.ToLowerInvariant());
        }

        return null;
    }

    private static void AppendExtra(List<string> extras, string? brief, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var line = $"{label}: {value.Trim()}";
        if (!string.IsNullOrWhiteSpace(brief)
            && brief.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        extras.Add(line);
    }

    private static bool IsPersonLike(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var t = name.Trim();
        if (t.Length is < 2 or > 60)
        {
            return false;
        }

        // Single capitalized token or "First Last"
        var parts = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        return parts.All(p => p.Length >= 2 && char.IsUpper(p[0]) && p.Skip(1).Any(char.IsLetter));
    }

    private static bool LooksImperative(string text)
    {
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(first))
        {
            return false;
        }

        var verb = first.Trim().ToLowerInvariant().TrimEnd(',', '.');
        return ImperativeVerbs.Contains(verb);
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
            "\n",
        ];

        var cut = title.Length;
        foreach (var sep in separators)
        {
            var idx = title.IndexOf(sep, StringComparison.Ordinal);
            if (idx >= TitleShortenHelper.MinUsefulLength && idx < cut)
            {
                cut = idx;
            }
        }

        return cut < title.Length ? title[..cut].Trim() : title;
    }

    private static string StripTrailingNoise(string title)
    {
        var t = title.Trim().TrimEnd('.', '!', '?');
        return t;
    }

    private static string TruncateAtWord(string title, int maxLength)
    {
        if (title.Length <= maxLength)
        {
            return title;
        }

        var slice = title[..maxLength].TrimEnd();
        var lastSpace = slice.LastIndexOf(' ');
        if (lastSpace >= TitleShortenHelper.MinUsefulLength)
        {
            slice = slice[..lastSpace].TrimEnd();
        }

        return slice;
    }

    private static string TitleCaseLight(string text)
    {
        var trimmed = CollapseWhitespace(text.Trim().TrimEnd('.', '!', '?'));
        if (trimmed.Length == 0)
        {
            return text.Trim();
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length == 0)
            {
                continue;
            }

            if (i > 0 && MinorWords.Contains(w.ToLowerInvariant()))
            {
                words[i] = w.ToLowerInvariant();
                continue;
            }

            // Preserve ALLCAPS acronyms (2–5 letters) and mixed tokens with digits.
            if (w.Length <= 5 && w.All(char.IsUpper))
            {
                continue;
            }

            words[i] = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
        }

        return string.Join(' ', words);
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(
            ' ',
            text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return CollapseWhitespace(text.Trim());
    }

    private static readonly HashSet<string> MinorWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "for", "on", "in", "at", "by", "with", "from",
    };

    private static readonly HashSet<string> ImperativeVerbs = new(StringComparer.Ordinal)
    {
        "call", "email", "send", "ask", "check", "follow", "schedule", "book", "order", "pay",
        "review", "update", "confirm", "draft", "file", "submit", "chase", "ping", "remind",
        "meet", "visit", "inspect", "fix", "ship", "close", "open", "prepare", "write",
    };

    [GeneratedRegex(
        @"\bwaiting\s+(?:on|for)\s+([^.\n;]{2,120})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WaitingOnRegex();

    [GeneratedRegex(
        @"\b(?:follow[\s-]?up(?:\s+with|\s+on)?|call|email|ping|chase|remind)\s+(.{4,120})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpActionRegex();

    [GeneratedRegex(
        @"\b(?:need\s+to|needs\s+to|should|must)\s+(.{4,120})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NeedToActionRegex();

    [GeneratedRegex(
        @"\b(?:from|w\/|with)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex FromPersonRegex();

    [GeneratedRegex(
        @"\b(?:ask|tell|call|email|ping)\s+([A-Z][a-z]+(?:\s+[A-Z][a-z]+)?)\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex AskPersonRegex();

    [GeneratedRegex(
        @"\b(?:unit|apt|apartment|#)\s*[A-Za-z0-9-]{1,8}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitRegex();

    [GeneratedRegex(
        @"\b(?:suite|ste)\s*[A-Za-z0-9-]{1,8}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuiteRegex();

    [GeneratedRegex(
        @"\b20\d{2}-\d{2}-\d{2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(
        @"\b(?:0?[1-9]|1[0-2])[\/\-](?:0?[1-9]|[12]\d|3[01])(?:[\/\-](?:20)?\d{2})?\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsDateRegex();

    [GeneratedRegex(
        @"\b(?:due|by|before)\s+(today|tomorrow)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RelativeDueRegex();

    [GeneratedRegex(
        @"\b(?:due|by|before)\s+(monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ByWeekdayRegex();
}
