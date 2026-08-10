using System.Globalization;
using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Contacts;

/// <summary>
/// Heuristic signature parse only (no LLM). Looks at the trailing body block for title and phones.
/// </summary>
public static class SignatureHeuristic
{
    private static readonly Regex PhoneRegex = new(
        @"(?<label>mobile|cell|m|direct|office|tel|phone|ph)?\s*[:\-]?\s*(?<num>\+?\(?\d[\d\s\-().]{6,}\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DigitsOnly = new(@"\D", RegexOptions.Compiled);

    private static readonly string[] TitleHints =
    [
        "manager", "director", "engineer", "analyst", "coordinator", "specialist",
        "lead", "vp", "president", "officer", "consultant", "architect", "account",
        "associate", "executive", "supervisor", "principal",
    ];

    public sealed class SignatureFacts
    {
        public string? Title { get; init; }

        public string? OrganizationName { get; init; }

        public string? DirectPhone { get; init; }

        public string? MobilePhone { get; init; }

        public string? OfficePhone { get; init; }
    }

    public static SignatureFacts Parse(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return new SignatureFacts();
        }

        var block = ExtractSignatureBlock(bodyText);
        if (block.Count == 0)
        {
            return new SignatureFacts();
        }

        string? title = null;
        string? org = null;
        string? mobile = null;
        string? direct = null;
        string? office = null;

        foreach (var rawLine in block)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (Match match in PhoneRegex.Matches(line))
            {
                var digits = DigitsOnly.Replace(match.Groups["num"].Value, string.Empty);
                if (digits.Length < 10)
                {
                    continue;
                }

                var normalized = NormalizePhoneDisplay(match.Groups["num"].Value);
                var label = match.Groups["label"].Value.ToLowerInvariant();
                if (label is "mobile" or "cell" or "m")
                {
                    mobile ??= normalized;
                }
                else if (label is "direct")
                {
                    direct ??= normalized;
                }
                else if (label is "office")
                {
                    office ??= normalized;
                }
                else if (label is "tel" or "phone" or "ph" or "")
                {
                    // Unlabeled signature lines are usually the person's direct/mobile.
                    direct ??= normalized;
                    mobile ??= normalized;
                }
            }

            // Bare line that is mostly a phone (common in corporate sigs without a label).
            if (mobile is null && direct is null)
            {
                var bare = NormalizePhoneDigits(line);
                if (bare is not null && DigitsOnly.Replace(line, string.Empty).Length >= 10
                    && line.Length <= 24)
                {
                    mobile = NormalizePhoneDisplay(line);
                }
            }

            if (title is null && LooksLikeTitle(line) && !PhoneRegex.IsMatch(line))
            {
                title = line;
                continue;
            }

            if (org is null
                && !LooksLikeTitle(line)
                && !PhoneRegex.IsMatch(line)
                && !line.Contains('@', StringComparison.Ordinal)
                && line.Length is >= 2 and <= 80
                && !LooksLikeNameOnly(line, block))
            {
                // Prefer lines with Inc/LLC/Corp or Title Case company-ish tokens later in the block.
                if (ContainsOrgHint(line) || block.IndexOf(rawLine) > 0)
                {
                    org = line;
                }
            }
        }

        return new SignatureFacts
        {
            Title = title,
            OrganizationName = org,
            DirectPhone = direct,
            MobilePhone = mobile,
            OfficePhone = office,
        };
    }

    public static string? NormalizePhoneDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = DigitsOnly.Replace(value, string.Empty);
        if (digits.Length == 11 && digits.StartsWith('1'))
        {
            digits = digits[1..];
        }

        return digits.Length >= 10 ? digits : null;
    }

    private static List<string> ExtractSignatureBlock(string bodyText)
    {
        var normalized = bodyText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').Select(l => l.TrimEnd()).ToList();

        // Drop quoted reply tails.
        var cut = lines.FindIndex(l => l.StartsWith("-----Original Message-----", StringComparison.OrdinalIgnoreCase)
            || l.StartsWith("From:", StringComparison.OrdinalIgnoreCase) && l.Contains('@'));
        if (cut > 0)
        {
            lines = lines.Take(cut).ToList();
        }

        var dashIdx = lines.FindLastIndex(l => l.Trim() is "--" or "---" or "-- ");
        if (dashIdx >= 0 && dashIdx < lines.Count - 1)
        {
            return lines.Skip(dashIdx + 1).Where(l => l.Trim().Length > 0).Take(12).ToList();
        }

        // Fallback: last non-empty chunk (up to 12 lines) after a blank line near the end.
        var nonEmpty = lines.Select((l, i) => (Line: l, Index: i)).Where(x => x.Line.Trim().Length > 0).ToList();
        if (nonEmpty.Count == 0)
        {
            return [];
        }

        var start = Math.Max(0, nonEmpty.Count - 12);
        for (var i = nonEmpty.Count - 2; i >= Math.Max(0, nonEmpty.Count - 12); i--)
        {
            if (nonEmpty[i + 1].Index - nonEmpty[i].Index > 1)
            {
                start = i + 1;
                break;
            }
        }

        return nonEmpty.Skip(start).Select(x => x.Line).Take(12).ToList();
    }

    private static bool LooksLikeTitle(string line)
    {
        var lower = line.ToLowerInvariant();
        if (lower.Contains('@') || PhoneRegex.IsMatch(line))
        {
            return false;
        }

        if (line.Length > 80)
        {
            return false;
        }

        return TitleHints.Any(h => lower.Contains(h, StringComparison.Ordinal));
    }

    private static bool ContainsOrgHint(string line)
    {
        var lower = line.ToLowerInvariant();
        return lower.Contains("inc", StringComparison.Ordinal)
            || lower.Contains("llc", StringComparison.Ordinal)
            || lower.Contains("corp", StringComparison.Ordinal)
            || lower.Contains("ltd", StringComparison.Ordinal)
            || lower.Contains("company", StringComparison.Ordinal);
    }

    private static bool LooksLikeNameOnly(string line, List<string> block)
    {
        // First signature line is often the person's name; don't treat as org.
        var first = block.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
        return string.Equals(first, line.Trim(), StringComparison.Ordinal);
    }

    private static string NormalizePhoneDisplay(string raw)
    {
        var trimmed = raw.Trim();
        var digits = NormalizePhoneDigits(trimmed);
        if (digits is null)
        {
            return trimmed;
        }

        if (digits.Length == 10)
        {
            return string.Create(CultureInfo.InvariantCulture, $"({digits[..3]}) {digits[3..6]}-{digits[6..]}");
        }

        return trimmed;
    }
}
