using System.Text.RegularExpressions;

namespace Orbit.Core.Updates;

/// <summary>
/// Lightweight semver compare for Orbit tags like <c>v0.2.0</c> or informational
/// versions like <c>0.1.0-phase17</c>. Pre-release labels sort below the same core.
/// </summary>
public static partial class SemVer
{
    [GeneratedRegex(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SemVerRegex();

    public static bool TryParse(string? text, out SemVerValue value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var plus = trimmed.IndexOf('+');
        if (plus > 0)
        {
            trimmed = trimmed[..plus];
        }

        var match = SemVerRegex().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        value = new SemVerValue(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["pre"].Success ? match.Groups["pre"].Value : null);
        return true;
    }

    public static int Compare(string? left, string? right)
    {
        var leftOk = TryParse(left, out var l);
        var rightOk = TryParse(right, out var r);
        if (!leftOk && !rightOk)
        {
            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        }

        if (!leftOk)
        {
            return -1;
        }

        if (!rightOk)
        {
            return 1;
        }

        return l.CompareTo(r);
    }

    public static bool IsNewer(string? candidate, string? current) =>
        Compare(candidate, current) > 0;
}

public readonly record struct SemVerValue(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemVerValue>
{
    public int CompareTo(SemVerValue other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        // No pre-release beats any pre-release (1.0.0 > 1.0.0-rc.1).
        var leftPre = PreRelease;
        var rightPre = other.PreRelease;
        if (string.IsNullOrEmpty(leftPre) && string.IsNullOrEmpty(rightPre))
        {
            return 0;
        }

        if (string.IsNullOrEmpty(leftPre))
        {
            return 1;
        }

        if (string.IsNullOrEmpty(rightPre))
        {
            return -1;
        }

        return string.Compare(leftPre, rightPre, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() =>
        string.IsNullOrEmpty(PreRelease)
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}
