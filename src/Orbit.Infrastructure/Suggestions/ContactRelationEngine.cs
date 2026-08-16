using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Suggestions;

/// <summary>
/// Heuristic org-chart / reporting suggestions when new contacts are observed.
/// Proposes pending suggestions only — never writes reporting edges until accept.
/// </summary>
public sealed class ContactRelationEngine
{
    private const int MaxSuggestionsPerRun = 5;

    private static readonly (string Token, int Rank)[] TitleRanks =
    [
        ("chief", 100),
        ("ceo", 100),
        ("cto", 95),
        ("cfo", 95),
        ("coo", 95),
        ("president", 90),
        ("vp", 80),
        ("vice president", 80),
        ("director", 70),
        ("head of", 65),
        ("manager", 55),
        ("lead", 50),
        ("principal", 48),
        ("senior", 40),
        ("account rep", 30),
        ("representative", 28),
        ("associate", 25),
        ("analyst", 22),
        ("coordinator", 20),
        ("assistant", 15),
        ("intern", 5),
    ];

    private readonly SqliteConnectionFactory _factory;
    private readonly SuggestionStore _suggestions;

    public ContactRelationEngine(SqliteConnectionFactory factory, SuggestionStore suggestions)
    {
        _factory = factory;
        _suggestions = suggestions;
    }

    public IReadOnlyList<AgentSuggestionRecord> SuggestReportingForPeople(IReadOnlyList<string> personIds)
    {
        if (personIds.Count == 0)
        {
            return [];
        }

        var created = new List<AgentSuggestionRecord>();
        using var connection = _factory.CreateConnection();

        foreach (var personId in personIds.Distinct(StringComparer.Ordinal))
        {
            if (created.Count >= MaxSuggestionsPerRun)
            {
                break;
            }

            var anchor = LoadPerson(connection, personId);
            if (anchor is null || string.IsNullOrWhiteSpace(anchor.OrganizationId))
            {
                continue;
            }

            var colleagues = LoadOrgColleagues(connection, anchor.OrganizationId, personId);
            if (colleagues.Count == 0)
            {
                continue;
            }

            var existing = LoadExistingReportingPairs(connection, anchor.OrganizationId);
            var anchorRank = RankTitle(anchor.Title);

            foreach (var other in colleagues)
            {
                if (created.Count >= MaxSuggestionsPerRun)
                {
                    break;
                }

                var otherRank = RankTitle(other.Title);
                if (anchorRank == 0 && otherRank == 0)
                {
                    continue;
                }

                string juniorId;
                string seniorId;
                string juniorName;
                string seniorName;
                if (otherRank > anchorRank + 8)
                {
                    juniorId = anchor.Id;
                    juniorName = anchor.DisplayName;
                    seniorId = other.Id;
                    seniorName = other.DisplayName;
                }
                else if (anchorRank > otherRank + 8)
                {
                    juniorId = other.Id;
                    juniorName = other.DisplayName;
                    seniorId = anchor.Id;
                    seniorName = anchor.DisplayName;
                }
                else
                {
                    continue;
                }

                if (existing.Contains((juniorId, seniorId))
                    || existing.Contains((seniorId, juniorId)))
                {
                    continue;
                }

                if (_suggestions.HasPendingForPayloadTokens(
                        SuggestionTypes.ReportingRelationship, juniorId, seniorId)
                    || _suggestions.WasDecidedForPayloadTokens(
                        SuggestionTypes.ReportingRelationship, juniorId, seniorId))
                {
                    continue;
                }

                var summary =
                    $"Does {juniorName} report to {seniorName} at {anchor.OrganizationName}?";
                var payload = JsonSerializer.Serialize(new
                {
                    personId = juniorId,
                    reportsToPersonId = seniorId,
                    organizationId = anchor.OrganizationId,
                });

                created.Add(_suggestions.Create(new CreateSuggestionRequest
                {
                    SuggestionType = SuggestionTypes.ReportingRelationship,
                    Summary = summary,
                    PayloadJson = payload,
                    GroupKey = SuggestionHygiene.ReportingRelationshipKey(juniorId, seniorId),
                    Confidence = 0.45,
                }));
                existing.Add((juniorId, seniorId));
            }
        }

        return created;
    }

    private static int RankTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var lower = title.Trim().ToLowerInvariant();
        var best = 0;
        foreach (var (token, rank) in TitleRanks)
        {
            if (lower.Contains(token, StringComparison.Ordinal) && rank > best)
            {
                best = rank;
            }
        }

        return best;
    }

    private static PersonOrgRow? LoadPerson(SqliteConnection connection, string personId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.display_name,
                   (SELECT m.title FROM organization_memberships m
                    WHERE m.person_id = p.id AND m.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1),
                   (SELECT m.organization_id FROM organization_memberships m
                    WHERE m.person_id = p.id AND m.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1),
                   (SELECT o.name FROM organization_memberships m
                    INNER JOIN organizations o ON o.id = m.organization_id
                    WHERE m.person_id = p.id AND m.archived_at IS NULL AND o.archived_at IS NULL
                    ORDER BY m.updated_at DESC LIMIT 1)
            FROM people p
            WHERE p.id = $id AND p.archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", personId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PersonOrgRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static List<PersonOrgRow> LoadOrgColleagues(
        SqliteConnection connection,
        string organizationId,
        string excludePersonId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT p.id, p.display_name, m.title, m.organization_id, o.name
            FROM organization_memberships m
            INNER JOIN people p ON p.id = m.person_id
            INNER JOIN organizations o ON o.id = m.organization_id
            WHERE m.organization_id = $org
              AND m.archived_at IS NULL
              AND p.archived_at IS NULL
              AND o.archived_at IS NULL
              AND p.id <> $exclude
            ORDER BY p.display_name COLLATE NOCASE
            LIMIT 40;
            """;
        cmd.Parameters.AddWithValue("$org", organizationId);
        cmd.Parameters.AddWithValue("$exclude", excludePersonId);
        var list = new List<PersonOrgRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PersonOrgRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return list;
    }

    private static HashSet<(string Junior, string Senior)> LoadExistingReportingPairs(
        SqliteConnection connection,
        string organizationId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT person_id, reports_to_person_id
            FROM reporting_relationships
            WHERE archived_at IS NULL
              AND (organization_id = $org OR organization_id IS NULL);
            """;
        cmd.Parameters.AddWithValue("$org", organizationId);
        var set = new HashSet<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add((reader.GetString(0), reader.GetString(1)));
        }

        return set;
    }

    private sealed record PersonOrgRow(
        string Id,
        string DisplayName,
        string? Title,
        string? OrganizationId,
        string? OrganizationName);
}
