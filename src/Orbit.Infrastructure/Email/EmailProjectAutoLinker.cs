using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Deterministic project match from email subject/body (e.g. a street number → project name).
/// Used so duty wake does not depend on Hermes rediscovering the email.
/// </summary>
public static class EmailProjectAutoLinker
{
    public static IReadOnlyList<(string ProjectId, string Name, double Confidence)> MatchProjects(
        SqliteConnectionFactory factory,
        string? subject,
        string? bodyPreview,
        int max = 3)
    {
        var haystack = $"{subject} {bodyPreview}";
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY length(name) DESC;
            """;

        var matches = new List<(string ProjectId, string Name, double Confidence)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? null : reader.GetString(2);

            double? confidence = null;
            if (ContainsToken(haystack, name))
            {
                confidence = name.Length >= 4 ? 0.9 : 0.75;
            }
            else if (!string.IsNullOrWhiteSpace(code) && ContainsToken(haystack, code))
            {
                confidence = 0.85;
            }
            else
            {
                // Street number / short property token inside a subject line
                foreach (var token in SplitSignificantTokens(name))
                {
                    if (token.Length >= 3 && ContainsToken(haystack, token))
                    {
                        confidence = token.All(char.IsDigit) ? 0.88 : 0.72;
                        break;
                    }
                }
            }

            if (confidence is not null)
            {
                matches.Add((id, name, confidence.Value));
            }
        }

        return matches
            .OrderByDescending(m => m.Confidence)
            .ThenByDescending(m => m.Name.Length)
            .Take(Math.Clamp(max, 1, 5))
            .ToList();
    }

    private static IEnumerable<string> SplitSignificantTokens(string name) =>
        name.Split([' ', '-', '/', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3);

    private static bool ContainsToken(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        return haystack.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
