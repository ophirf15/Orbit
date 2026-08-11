using Microsoft.Data.Sqlite;

namespace Orbit.Infrastructure.Data;

/// <summary>
/// Scores haystacks and proposed names against project name, code, and operator aliases.
/// Aliases are operator data only — never baked into product defaults.
/// </summary>
public static class ProjectIdentityMatcher
{
    /// <summary>Refuse silent create when the best candidate scores at or above this.</summary>
    public const double NearDupeThreshold = 0.85;

    /// <summary>Minimum score to surface as a disambiguation candidate.</summary>
    public const double CandidateFloor = 0.45;

    public static string Normalize(string? value)
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

    public static IReadOnlyList<ProjectMatchCandidate> MatchHaystack(
        SqliteConnectionFactory factory,
        string? haystack,
        int max = 5)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        using var connection = factory.CreateConnection();
        return RankAgainstHaystack(LoadIdentities(connection), haystack, max);
    }

    public static IReadOnlyList<ProjectMatchCandidate> MatchHaystack(
        SqliteConnection connection,
        string? haystack,
        int max = 5)
    {
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        return RankAgainstHaystack(LoadIdentities(connection), haystack, max);
    }

    /// <summary>Near-duplicate check for create-project: compare proposed name to existing identities.</summary>
    public static IReadOnlyList<ProjectMatchCandidate> FindNearDuplicates(
        SqliteConnectionFactory factory,
        string? proposedName,
        int max = 5)
    {
        var normalizedProposed = Normalize(proposedName);
        if (normalizedProposed.Length == 0)
        {
            return [];
        }

        using var connection = factory.CreateConnection();
        var identities = LoadIdentities(connection);
        var scored = new List<ProjectMatchCandidate>();

        foreach (var project in identities)
        {
            var best = ScoreProposedAgainstProject(normalizedProposed, proposedName!.Trim(), project);
            if (best is not null && best.Score >= CandidateFloor)
            {
                scored.Add(best);
            }
        }

        return scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(max, 1, 10))
            .ToList();
    }

    public static IReadOnlyList<ProjectIdentity> LoadIdentities(SqliteConnection connection)
    {
        var aliasesByProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var aliasCmd = connection.CreateCommand())
        {
            aliasCmd.CommandText =
                """
                SELECT project_id, alias
                FROM project_aliases
                ORDER BY length(alias) DESC;
                """;
            using var aliasReader = aliasCmd.ExecuteReader();
            while (aliasReader.Read())
            {
                var projectId = aliasReader.GetString(0);
                var alias = aliasReader.GetString(1);
                if (!aliasesByProject.TryGetValue(projectId, out var aliasesForProject))
                {
                    aliasesForProject = [];
                    aliasesByProject[projectId] = aliasesForProject;
                }

                aliasesForProject.Add(alias);
            }
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code
            FROM projects
            WHERE archived_at IS NULL
            ORDER BY length(name) DESC;
            """;

        var list = new List<ProjectIdentity>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? null : reader.GetString(2);
            aliasesByProject.TryGetValue(id, out var aliases);
            list.Add(new ProjectIdentity(id, name, code, aliases ?? (IReadOnlyList<string>)[]));
        }

        return list;
    }

    private static IReadOnlyList<ProjectMatchCandidate> RankAgainstHaystack(
        IReadOnlyList<ProjectIdentity> identities,
        string haystack,
        int max)
    {
        var scored = new List<ProjectMatchCandidate>();
        foreach (var project in identities)
        {
            var best = ScoreHaystackAgainstProject(haystack, project);
            if (best is not null)
            {
                scored.Add(best);
            }
        }

        return scored
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Name.Length)
            .Take(Math.Clamp(max, 1, 10))
            .ToList();
    }

    private static ProjectMatchCandidate? ScoreHaystackAgainstProject(string haystack, ProjectIdentity project)
    {
        ProjectMatchCandidate? best = null;

        void Consider(double score, string reason)
        {
            if (best is null || score > best.Score)
            {
                best = new ProjectMatchCandidate(project.Id, project.Name, score, reason);
            }
        }

        if (ContainsToken(haystack, project.Name))
        {
            Consider(project.Name.Length >= 4 ? 0.9 : 0.75, "name");
        }

        if (!string.IsNullOrWhiteSpace(project.Code) && ContainsToken(haystack, project.Code))
        {
            Consider(0.85, "code");
        }

        foreach (var alias in project.Aliases)
        {
            if (ContainsToken(haystack, alias))
            {
                Consider(alias.Length >= 4 ? 0.88 : 0.8, "alias");
            }
        }

        if (best is null)
        {
            foreach (var token in SplitSignificantTokens(project.Name))
            {
                if (token.Length >= 3 && ContainsToken(haystack, token))
                {
                    Consider(token.All(char.IsDigit) ? 0.88 : 0.72, "name_token");
                    break;
                }
            }
        }

        return best;
    }

    private static ProjectMatchCandidate? ScoreProposedAgainstProject(
        string normalizedProposed,
        string rawProposed,
        ProjectIdentity project)
    {
        ProjectMatchCandidate? best = null;

        void Consider(double score, string reason)
        {
            if (best is null || score > best.Score)
            {
                best = new ProjectMatchCandidate(project.Id, project.Name, score, reason);
            }
        }

        var nameNorm = Normalize(project.Name);
        if (nameNorm.Length > 0 && string.Equals(normalizedProposed, nameNorm, StringComparison.Ordinal))
        {
            Consider(1.0, "exact_name");
        }
        else if (nameNorm.Length >= 4
                 && (normalizedProposed.Contains(nameNorm, StringComparison.Ordinal)
                     || nameNorm.Contains(normalizedProposed, StringComparison.Ordinal)))
        {
            Consider(0.9, "name_overlap");
        }

        if (!string.IsNullOrWhiteSpace(project.Code))
        {
            var codeNorm = Normalize(project.Code);
            if (codeNorm.Length > 0 && string.Equals(normalizedProposed, codeNorm, StringComparison.Ordinal))
            {
                Consider(0.95, "exact_code");
            }
        }

        foreach (var alias in project.Aliases)
        {
            var aliasNorm = Normalize(alias);
            if (aliasNorm.Length == 0)
            {
                continue;
            }

            if (string.Equals(normalizedProposed, aliasNorm, StringComparison.Ordinal))
            {
                Consider(1.0, "exact_alias");
            }
            else if (aliasNorm.Length >= 3
                     && (normalizedProposed.Contains(aliasNorm, StringComparison.Ordinal)
                         || aliasNorm.Contains(normalizedProposed, StringComparison.Ordinal)))
            {
                Consider(0.88, "alias_overlap");
            }
        }

        // Token-level: proposed equals a significant name token (e.g. "Widget" vs "Acme Widget Co")
        foreach (var token in SplitSignificantTokens(project.Name))
        {
            var tokenNorm = Normalize(token);
            if (tokenNorm.Length >= 3
                && string.Equals(normalizedProposed, tokenNorm, StringComparison.Ordinal))
            {
                Consider(0.9, "name_token");
            }
        }

        _ = rawProposed;
        return best;
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

public sealed record ProjectIdentity(
    string Id,
    string Name,
    string? Code,
    IReadOnlyList<string> Aliases);

public sealed record ProjectMatchCandidate(
    string ProjectId,
    string Name,
    double Score,
    string Reason);
