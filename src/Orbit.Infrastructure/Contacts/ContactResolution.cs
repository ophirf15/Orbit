using System.Text.RegularExpressions;

namespace Orbit.Infrastructure.Contacts;

/// <summary>
/// Entity resolution helpers. Exact email = same person. Never merge on name alone.
/// </summary>
public static class ContactResolution
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@(?<domain>[^@\s]+\.[^@\s]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> FreeMailDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com",
        "yahoo.com", "icloud.com", "me.com", "aol.com", "proton.me", "protonmail.com",
        "msn.com", "ymail.com", "mail.com",
    };

    public static string? NormalizeEmail(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var trimmed = address.Trim().Trim('<', '>');
        if (!trimmed.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.ToLowerInvariant();
    }

    public static string? ExtractDomain(string? email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return null;
        }

        var match = EmailRegex.Match(normalized);
        return match.Success ? match.Groups["domain"].Value.ToLowerInvariant() : null;
    }

    public static bool IsFreeMailDomain(string? domain) =>
        !string.IsNullOrWhiteSpace(domain) && FreeMailDomains.Contains(domain);

    public static string? NormalizePhone(string? value) => SignatureHeuristic.NormalizePhoneDigits(value);

    public static string DisplayNameFromParticipant(string? displayName, string? email)
    {
        if (!string.IsNullOrWhiteSpace(displayName)
            && !displayName.Contains('@', StringComparison.Ordinal))
        {
            return displayName.Trim();
        }

        var normalized = NormalizeEmail(email);
        if (normalized is null)
        {
            return string.IsNullOrWhiteSpace(displayName) ? "Unknown" : displayName.Trim();
        }

        var local = normalized.Split('@')[0];
        var parts = local.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return local;
        }

        return string.Join(' ', parts.Select(Capitalize));
    }

    public static (string? Given, string? Family) SplitName(string displayName)
    {
        var parts = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return (null, null);
        }

        if (parts.Length == 1)
        {
            return (parts[0], null);
        }

        return (parts[0], string.Join(' ', parts.Skip(1)));
    }

    public static string OrganizationNameFromDomain(string domain)
    {
        var root = domain.Split('.')[0];
        return string.IsNullOrWhiteSpace(root) ? domain : Capitalize(root);
    }

    public static bool NamesSimilar(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var na = NormalizeNameKey(a);
        var nb = NormalizeNameKey(b);
        return string.Equals(na, nb, StringComparison.Ordinal);
    }

    private static string NormalizeNameKey(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string Capitalize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return value.ToUpperInvariant();
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }
}
