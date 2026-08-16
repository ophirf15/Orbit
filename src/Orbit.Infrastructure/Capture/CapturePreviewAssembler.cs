using Orbit.Core.Workbench;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;

namespace Orbit.Infrastructure.Capture;

/// <summary>
/// Assembles a capture preview: field proposals + project identity match with explainability.
/// </summary>
public sealed class CapturePreviewAssembler
{
    /// <summary>Auto-select the top identity match at or above this score.</summary>
    public const double AutoSelectFloor = 0.75;

    private readonly SqliteConnectionFactory _factory;
    private readonly ProjectFolderStore? _folders;

    public CapturePreviewAssembler(
        SqliteConnectionFactory factory,
        ProjectFolderStore? folders = null)
    {
        _factory = factory;
        _folders = folders;
    }

    public CapturePreviewResult Assemble(string? text, string? defaultProjectId = null)
    {
        var proposal = CapturePreviewProposer.Propose(text);
        var haystack = proposal.OriginalText.Trim();
        if (haystack.Length == 0 && string.IsNullOrWhiteSpace(defaultProjectId))
        {
            return CapturePreviewResult.FromProposal(proposal, matched: null, candidates: []);
        }

        var ranked = haystack.Length == 0
            ? (IReadOnlyList<ProjectMatchCandidate>)[]
            : RankWithExtras(haystack);

        CapturePreviewProjectMatch? matched = null;
        if (!string.IsNullOrWhiteSpace(defaultProjectId))
        {
            var scoped = ResolveProject(defaultProjectId.Trim());
            if (scoped is not null)
            {
                var fromHaystack = ranked.FirstOrDefault(c =>
                    string.Equals(c.ProjectId, scoped.ProjectId, StringComparison.Ordinal));
                if (fromHaystack is not null && fromHaystack.Score >= AutoSelectFloor)
                {
                    matched = ToMatch(fromHaystack, autoSelected: true);
                }
                else
                {
                    matched = new CapturePreviewProjectMatch(
                        scoped.ProjectId,
                        scoped.Name,
                        Score: fromHaystack?.Score ?? 1.0,
                        Reason: "scoped",
                        ReasonLabel: CaptureMatchReasonFormatter.Format("scoped"),
                        AutoSelected: true);
                }
            }
        }

        if (matched is null && ranked.Count > 0 && ranked[0].Score >= AutoSelectFloor)
        {
            matched = ToMatch(ranked[0], autoSelected: true);
        }

        var enriched = EnrichFromMatchedProject(proposal, matched);

        var candidates = ranked
            .Take(5)
            .Select(c => ToMatch(c, autoSelected: matched is not null
                && string.Equals(matched.ProjectId, c.ProjectId, StringComparison.Ordinal)))
            .ToList();

        return CapturePreviewResult.FromProposal(enriched, matched, candidates);
    }

    private IReadOnlyList<ProjectMatchCandidate> RankWithExtras(string haystack)
    {
        using var connection = _factory.CreateConnection();
        var ranked = ProjectIdentityMatcher.MatchHaystack(connection, haystack, max: 8).ToList();

        if (_folders is not null)
        {
            foreach (var folder in _folders.ListAll())
            {
                var leaf = FolderLeaf(folder.RootPath);
                if (leaf.Length < 4 || !haystack.Contains(leaf, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Consider(ranked, folder.ProjectId, ResolveName(connection, folder.ProjectId), 0.86, "folder");
            }
        }

        foreach (var project in ProjectIdentityMatcher.LoadIdentities(connection))
        {
            var dossier = LoadDossier(connection, project.Id);
            if (dossier is null || dossier.IsStructurallyEmpty)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(dossier.Address)
                && haystack.Contains(dossier.Address.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Consider(ranked, project.Id, project.Name, 0.87, "address");
            }

            foreach (var contact in dossier.CriticalContacts)
            {
                if (string.IsNullOrWhiteSpace(contact.Name) || contact.Name.Trim().Length < 3)
                {
                    continue;
                }

                if (haystack.Contains(contact.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    Consider(ranked, project.Id, project.Name, 0.84, "contact");
                    break;
                }
            }
        }

        return ranked
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Name.Length)
            .Take(8)
            .ToList();
    }

    private CapturePreviewProposal EnrichFromMatchedProject(
        CapturePreviewProposal proposal,
        CapturePreviewProjectMatch? matched)
    {
        if (matched is null)
        {
            return proposal;
        }

        using var connection = _factory.CreateConnection();
        var dossier = LoadDossier(connection, matched.ProjectId);
        if (dossier is null || dossier.IsStructurallyEmpty)
        {
            return proposal;
        }

        var people = proposal.PeopleHint;
        var location = proposal.LocationHint;
        var haystack = proposal.OriginalText;

        if (string.IsNullOrWhiteSpace(people))
        {
            foreach (var contact in dossier.CriticalContacts)
            {
                if (!string.IsNullOrWhiteSpace(contact.Name)
                    && haystack.Contains(contact.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    people = contact.Name.Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(people)
                && !string.IsNullOrWhiteSpace(dossier.OwnerClient)
                && haystack.Contains(dossier.OwnerClient.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                people = dossier.OwnerClient.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(location)
            && !string.IsNullOrWhiteSpace(dossier.Address)
            && haystack.Contains(dossier.Address.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            location = dossier.Address.Trim();
        }

        if (string.Equals(people, proposal.PeopleHint, StringComparison.Ordinal)
            && string.Equals(location, proposal.LocationHint, StringComparison.Ordinal))
        {
            return proposal;
        }

        return proposal with { PeopleHint = people, LocationHint = location };
    }

    private CapturePreviewProjectMatch? ResolveProject(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name FROM projects
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", projectId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new CapturePreviewProjectMatch(
            reader.GetString(0),
            reader.GetString(1),
            Score: 1.0,
            Reason: "scoped",
            ReasonLabel: CaptureMatchReasonFormatter.Format("scoped"),
            AutoSelected: true);
    }

    private static string ResolveName(Microsoft.Data.Sqlite.SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM projects WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId);
        var name = cmd.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(name) ? projectId : name;
    }

    private static ProjectDossier? LoadDossier(Microsoft.Data.Sqlite.SqliteConnection connection, string projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT dossier_json FROM projects WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId);
        var json = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return ProjectDossier.Parse(json);
    }

    private static void Consider(
        List<ProjectMatchCandidate> ranked,
        string projectId,
        string name,
        double score,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var existing = ranked.FindIndex(c => string.Equals(c.ProjectId, projectId, StringComparison.Ordinal));
        if (existing >= 0)
        {
            if (score > ranked[existing].Score)
            {
                ranked[existing] = new ProjectMatchCandidate(projectId, name, score, reason);
            }

            return;
        }

        ranked.Add(new ProjectMatchCandidate(projectId, name, score, reason));
    }

    private static string FolderLeaf(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return string.Empty;
        }

        var trimmed = rootPath.Trim().TrimEnd('\\', '/');
        var idx = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        return idx >= 0 && idx < trimmed.Length - 1 ? trimmed[(idx + 1)..] : trimmed;
    }

    private static CapturePreviewProjectMatch ToMatch(ProjectMatchCandidate c, bool autoSelected) =>
        new(
            c.ProjectId,
            c.Name,
            c.Score,
            c.Reason,
            CaptureMatchReasonFormatter.Format(c.Reason),
            autoSelected);
}

public sealed record CapturePreviewProjectMatch(
    string ProjectId,
    string Name,
    double Score,
    string Reason,
    string ReasonLabel,
    bool AutoSelected);

public sealed record CapturePreviewResult(
    string OriginalText,
    string Title,
    string? Brief,
    string? NextAction,
    string? DueHint,
    string? WaitingOnHint,
    string? PeopleHint,
    string? LocationHint,
    string Source,
    CapturePreviewProjectMatch? MatchedProject,
    IReadOnlyList<CapturePreviewProjectMatch> Candidates)
{
    public static CapturePreviewResult FromProposal(
        CapturePreviewProposal proposal,
        CapturePreviewProjectMatch? matched,
        IReadOnlyList<CapturePreviewProjectMatch> candidates) =>
        new(
            proposal.OriginalText,
            proposal.Title,
            proposal.Brief,
            proposal.NextAction,
            proposal.DueHint,
            proposal.WaitingOnHint,
            proposal.PeopleHint,
            proposal.LocationHint,
            proposal.Source,
            matched,
            candidates);
}
