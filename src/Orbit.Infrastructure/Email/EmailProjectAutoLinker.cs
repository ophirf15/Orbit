using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// Deterministic project match from email subject/body (name, code, operator aliases).
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
        return ProjectIdentityMatcher.MatchHaystack(factory, haystack, max)
            .Select(c => (c.ProjectId, c.Name, c.Score))
            .ToList();
    }

    /// <summary>Ranked candidates with match reason for disambiguation payloads.</summary>
    public static IReadOnlyList<ProjectMatchCandidate> MatchCandidates(
        SqliteConnectionFactory factory,
        string? subject,
        string? bodyPreview,
        int max = 5) =>
        ProjectIdentityMatcher.MatchHaystack(factory, $"{subject} {bodyPreview}", max);
}
