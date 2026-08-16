namespace Orbit.Core.Workbench;

/// <summary>Confidence band for capture → open-task update matching.</summary>
public enum CaptureTaskMatchBand
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>Open task fields scored against capture text (pure / unit-testable).</summary>
public sealed record CaptureTaskCandidate(
    string TaskId,
    string Title,
    string? NextAction = null,
    string? Body = null);

/// <summary>One ranked match of capture text against an open task.</summary>
public sealed record CaptureTaskMatch(
    string TaskId,
    string Title,
    double Score,
    CaptureTaskMatchBand Band,
    string Reason);

/// <summary>
/// Scores capture text against candidate open tasks (title / nextAction / body excerpt).
/// Caller always confirms before appending — never silent overwrite.
/// </summary>
public static class CaptureTaskUpdateMatcher
{
    /// <summary>Propose a single “update existing” choice at or above this score.</summary>
    public const double HighThreshold = 0.85;

    /// <summary>Surface alternatives at or above this score; below → create new.</summary>
    public const double MediumThreshold = 0.55;

    /// <summary>Drop candidates below this floor from ranked results.</summary>
    public const double RankFloor = 0.35;

    public const int DefaultMaxResults = 5;

    public const int BodyExcerptChars = 480;

    /// <summary>
    /// Rank open tasks for a capture string. Highest score first.
    /// Empty capture or no candidates → empty list (caller creates new).
    /// </summary>
    public static IReadOnlyList<CaptureTaskMatch> Rank(
        string? captureText,
        IEnumerable<CaptureTaskCandidate> candidates,
        int max = DefaultMaxResults)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var capture = NormalizeWhitespace(captureText);
        if (capture.Length == 0)
        {
            return [];
        }

        var captureNorm = NormalizeKey(capture);
        var captureTokens = Tokenize(capture);
        var limit = Math.Clamp(max, 1, 20);
        var scored = new List<CaptureTaskMatch>();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.TaskId)
                || string.IsNullOrWhiteSpace(candidate.Title))
            {
                continue;
            }

            var match = ScoreOne(capture, captureNorm, captureTokens, candidate);
            if (match is not null && match.Score >= RankFloor)
            {
                scored.Add(match);
            }
        }

        return scored
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>Map a numeric score onto high / medium / low bands.</summary>
    public static CaptureTaskMatchBand BandFor(double score)
    {
        if (score >= HighThreshold)
        {
            return CaptureTaskMatchBand.High;
        }

        if (score >= MediumThreshold)
        {
            return CaptureTaskMatchBand.Medium;
        }

        return CaptureTaskMatchBand.Low;
    }

    /// <summary>
    /// High → single best update candidate; medium → up to three alternatives; low/none → create new.
    /// </summary>
    public static CaptureTaskMatchDecision Decide(IReadOnlyList<CaptureTaskMatch> ranked)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        if (ranked.Count == 0)
        {
            return CaptureTaskMatchDecision.CreateNew();
        }

        var best = ranked[0];
        return best.Band switch
        {
            CaptureTaskMatchBand.High => CaptureTaskMatchDecision.ProposeUpdate(best),
            CaptureTaskMatchBand.Medium => CaptureTaskMatchDecision.ShowAlternatives(
                ranked
                    .Where(m => m.Band is CaptureTaskMatchBand.High or CaptureTaskMatchBand.Medium)
                    .Take(3)
                    .ToList()),
            CaptureTaskMatchBand.Low => CaptureTaskMatchDecision.CreateNew(),
            _ => throw new InvalidOperationException($"Unhandled match band: {best.Band}"),
        };
    }

    private static CaptureTaskMatch? ScoreOne(
        string capture,
        string captureNorm,
        IReadOnlyList<string> captureTokens,
        CaptureTaskCandidate candidate)
    {
        var title = NormalizeWhitespace(candidate.Title);
        var titleNorm = NormalizeKey(title);
        if (titleNorm.Length == 0)
        {
            return null;
        }

        if (string.Equals(captureNorm, titleNorm, StringComparison.Ordinal))
        {
            return new CaptureTaskMatch(
                candidate.TaskId,
                title,
                1.0,
                CaptureTaskMatchBand.High,
                "exact_title");
        }

        if (IsMeaningfulContainment(captureNorm, titleNorm))
        {
            return new CaptureTaskMatch(
                candidate.TaskId,
                title,
                0.92,
                CaptureTaskMatchBand.High,
                "title_containment");
        }

        var titleTokens = Tokenize(title);
        var nextTokens = Tokenize(candidate.NextAction);
        var bodyTokens = Tokenize(Excerpt(candidate.Body));

        var titleCoverage = Coverage(captureTokens, titleTokens);
        var titleJaccard = Jaccard(captureTokens, titleTokens);
        var titleScore = (0.6 * titleCoverage) + (0.4 * titleJaccard);

        var nextCoverage = Coverage(captureTokens, nextTokens);
        var bodyCoverage = Coverage(captureTokens, bodyTokens);

        // Blend title / next / body; also allow a strong next-action or title-only path
        // so operational follow-ups still surface without an exact title rematch.
        var blend = (0.55 * titleScore) + (0.30 * nextCoverage) + (0.15 * bodyCoverage);
        var score = Math.Max(blend, titleScore * 0.95);
        if (nextCoverage >= 0.5)
        {
            score = Math.Max(score, (0.45 * titleScore) + (0.55 * nextCoverage));
        }

        var sharedNext = SharedCount(captureTokens, nextTokens);
        if (nextCoverage >= 0.55 && sharedNext >= 2)
        {
            // Strong next-move overlap is enough to offer update alternatives.
            score = Math.Max(score, MediumThreshold + (0.2 * titleScore));
        }

        var sharedTitle = SharedCount(captureTokens, titleTokens);
        if (sharedTitle >= 3)
        {
            score += 0.08;
        }
        else if (sharedTitle == 2 && titleCoverage >= 0.5)
        {
            score += 0.04;
        }

        // Near-duplicate titles that differ by a short prefix/suffix.
        if (titleNorm.Length >= 8
            && captureNorm.Length >= 8
            && (titleNorm.StartsWith(captureNorm, StringComparison.Ordinal)
                || captureNorm.StartsWith(titleNorm, StringComparison.Ordinal)))
        {
            score = Math.Max(score, 0.88);
        }

        score = Math.Clamp(score, 0, 1.0);
        if (score < RankFloor)
        {
            return null;
        }

        var reason = PickReason(titleCoverage, nextCoverage, bodyCoverage, sharedTitle);
        return new CaptureTaskMatch(
            candidate.TaskId,
            title,
            Math.Round(score, 4, MidpointRounding.AwayFromZero),
            BandFor(score),
            reason);
    }

    private static string PickReason(
        double titleCoverage,
        double nextCoverage,
        double bodyCoverage,
        int sharedTitle)
    {
        if (titleCoverage >= 0.5 || sharedTitle >= 2)
        {
            return "title_tokens";
        }

        if (nextCoverage >= titleCoverage && nextCoverage >= 0.35)
        {
            return "next_action_tokens";
        }

        if (bodyCoverage >= 0.35)
        {
            return "body_tokens";
        }

        return "weak_overlap";
    }

    private static bool IsMeaningfulContainment(string a, string b)
    {
        var shorter = a.Length <= b.Length ? a : b;
        var longer = a.Length <= b.Length ? b : a;
        if (shorter.Length < 8)
        {
            return false;
        }

        return longer.Contains(shorter, StringComparison.Ordinal);
    }

    private static string Excerpt(string? body)
    {
        var text = NormalizeWhitespace(body);
        if (text.Length <= BodyExcerptChars)
        {
            return text;
        }

        return text[..BodyExcerptChars];
    }

    private static double Coverage(IReadOnlyList<string> captureTokens, IReadOnlyList<string> fieldTokens)
    {
        if (captureTokens.Count == 0 || fieldTokens.Count == 0)
        {
            return 0;
        }

        var field = fieldTokens.ToHashSet(StringComparer.Ordinal);
        var hits = captureTokens.Count(field.Contains);
        return (double)hits / captureTokens.Count;
    }

    private static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var intersection = setA.Count(setB.Contains);
        var union = setA.Count;
        foreach (var t in setB)
        {
            setA.Add(t);
        }

        union = setA.Count;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static int SharedCount(IReadOnlyList<string> captureTokens, IReadOnlyList<string> fieldTokens)
    {
        if (captureTokens.Count == 0 || fieldTokens.Count == 0)
        {
            return 0;
        }

        var field = fieldTokens.ToHashSet(StringComparer.Ordinal);
        return captureTokens.Count(field.Contains);
    }

    internal static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        foreach (var raw in text.Split(
                     [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'', '/', '\\', '—', '-', '·'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length <= 2 || Stopwords.Contains(token) || !token.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            if (seen.Add(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value.Trim().ToLowerInvariant().ToCharArray();
        var buffer = new char[chars.Length];
        var n = 0;
        var prevSpace = false;
        foreach (var c in chars)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[n++] = c;
                prevSpace = false;
            }
            else if (char.IsWhiteSpace(c) || c is '-' or '_' or '/' or ',' or '.')
            {
                if (n > 0 && !prevSpace)
                {
                    buffer[n++] = ' ';
                    prevSpace = true;
                }
            }
        }

        return new string(buffer, 0, n).Trim();
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

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "and", "or", "of", "to", "for", "on", "in", "at", "by", "with",
        "from", "into", "about", "is", "are", "was", "were", "be", "been", "this", "that",
        "it", "as", "next", "move", "define",
    };
}

/// <summary>Decision after ranking — UI maps this to confirm dialogs; never auto-applies.</summary>
public sealed class CaptureTaskMatchDecision
{
    private CaptureTaskMatchDecision(
        CaptureTaskMatchIntent Intent,
        CaptureTaskMatch? Primary,
        IReadOnlyList<CaptureTaskMatch> Alternatives)
    {
        this.Intent = Intent;
        this.Primary = Primary;
        this.Alternatives = Alternatives;
    }

    public CaptureTaskMatchIntent Intent { get; }

    public CaptureTaskMatch? Primary { get; }

    public IReadOnlyList<CaptureTaskMatch> Alternatives { get; }

    public static CaptureTaskMatchDecision CreateNew() =>
        new(CaptureTaskMatchIntent.CreateNew, null, []);

    public static CaptureTaskMatchDecision ProposeUpdate(CaptureTaskMatch match) =>
        new(CaptureTaskMatchIntent.ProposeUpdate, match, [match]);

    public static CaptureTaskMatchDecision ShowAlternatives(IReadOnlyList<CaptureTaskMatch> alternatives) =>
        new(
            CaptureTaskMatchIntent.ShowAlternatives,
            alternatives.Count > 0 ? alternatives[0] : null,
            alternatives);
}

public enum CaptureTaskMatchIntent
{
    CreateNew = 0,
    ProposeUpdate = 1,
    ShowAlternatives = 2,
}

/// <summary>
/// Formats dated body appends matching merge-into-task attribution style.
/// Preserves original capture wording; never replaces existing body text.
/// </summary>
public static class CaptureTaskUpdateAppender
{
    public static string FormatStamp(
        string captureText,
        DateTimeOffset utcNow,
        string attribution = "From capture")
    {
        var text = (captureText ?? string.Empty).Trim();
        var label = string.IsNullOrWhiteSpace(attribution) ? "From capture" : attribution.Trim();
        return $"{label} ({utcNow.UtcDateTime:yyyy-MM-dd}): {text}";
    }

    public static string AppendToBody(
        string? currentBody,
        string captureText,
        DateTimeOffset utcNow,
        string attribution = "From capture")
    {
        var stamped = FormatStamp(captureText, utcNow, attribution);
        if (string.IsNullOrWhiteSpace(currentBody))
        {
            return stamped;
        }

        return $"{currentBody.TrimEnd()}\n\n{stamped}";
    }
}
