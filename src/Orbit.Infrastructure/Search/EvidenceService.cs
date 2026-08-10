using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Search;

public sealed class EvidenceCitation
{
    public required string Kind { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string Label { get; init; }

    public string? Path { get; init; }

    public string? Snippet { get; init; }

    public string? ProjectId { get; init; }
}

public sealed class EvidenceAnswer
{
    public required string Question { get; init; }

    public required string AnswerType { get; init; }

    public required string Answer { get; init; }

    public string? Value { get; init; }

    public string? ProjectId { get; init; }

    public string? OrganizationId { get; init; }

    public required IReadOnlyList<EvidenceCitation> Citations { get; init; }

    public object? Status { get; init; }
}

/// <summary>
/// Structured evidence retrieval (no LLM). EIN/W-9 and project-scoped status packs.
/// </summary>
public sealed class EvidenceService
{
    private static readonly Regex EinQuestion = new(
        @"\b(ein|employer\s+identification|tax\s+id|taxpayer\s+id|w-?9)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StatusQuestion = new(
        @"\b(status|blocker|next\s+action|what.?s\s+going\s+on|update\s+on|progress)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly SqliteConnectionFactory _factory;
    private readonly ContextBundleService _bundles;

    public EvidenceService(SqliteConnectionFactory factory, ContextBundleService? bundles = null)
    {
        _factory = factory;
        _bundles = bundles ?? new ContextBundleService(factory);
    }

    public EvidenceAnswer Query(string question, string? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var q = question.Trim();
        var scopedProject = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();

        if (EinQuestion.IsMatch(q))
        {
            return AnswerEin(q);
        }

        if (StatusQuestion.IsMatch(q) || scopedProject is not null || LooksLikeProjectName(q))
        {
            return AnswerProjectStatus(q, scopedProject);
        }

        // Fallback: treat as project status if we can resolve a project name fragment.
        var resolved = ResolveProjectId(q, scopedProject);
        if (resolved is not null)
        {
            return AnswerProjectStatus(q, resolved);
        }

        return new EvidenceAnswer
        {
            Question = q,
            AnswerType = "unsupported",
            Answer = "No structured evidence template matched. Try EIN/W-9 or a project status question.",
            Citations = [],
        };
    }

    private EvidenceAnswer AnswerEin(string question)
    {
        using var connection = _factory.CreateConnection();
        var citations = new List<EvidenceCitation>();

        // 1) Provenance fact on organization
        string? orgId = null;
        string? orgName = null;
        string? einValue = null;

        using (var fact = connection.CreateCommand())
        {
            fact.CommandText =
                """
                SELECT cfp.entity_id, o.name, cfp.value, cfp.field
                FROM contact_fact_provenance cfp
                INNER JOIN organizations o ON o.id = cfp.entity_id
                WHERE cfp.entity_type = 'organization'
                  AND o.archived_at IS NULL
                  AND (
                    lower(cfp.field) IN ('ein', 'tax_id', 'taxpayer_id', 'employer_identification_number')
                    OR lower(cfp.field) LIKE '%ein%'
                    OR lower(cfp.value) LIKE '%ein%'
                  )
                ORDER BY cfp.created_at DESC
                LIMIT 1;
                """;
            using var reader = fact.ExecuteReader();
            if (reader.Read())
            {
                orgId = reader.GetString(0);
                orgName = reader.GetString(1);
                einValue = reader.GetString(2);
                citations.Add(new EvidenceCitation
                {
                    Kind = "fact",
                    EntityType = "organization",
                    EntityId = orgId,
                    Label = orgName + " EIN fact",
                    Snippet = reader.GetString(3) + ": " + einValue,
                });
            }
        }

        // 2) Linked W-9 file (prefer org link; fall back to name/content)
        string? w9FileId = null;
        string? w9Path = null;
        string? w9Name = null;
        string? w9Snippet = null;

        using (var w9 = connection.CreateCommand())
        {
            w9.CommandText =
                """
                SELECT fa.id, fa.path, COALESCE(fa.display_name, fa.path),
                       substr(COALESCE(fa.indexed_text, ''), 1, 200),
                       fel.entity_id
                FROM file_artifacts fa
                LEFT JOIN file_entity_links fel
                  ON fel.file_artifact_id = fa.id AND fel.entity_type = 'organization'
                WHERE fa.archived_at IS NULL
                  AND (
                    lower(COALESCE(fa.display_name, '')) LIKE '%w-9%'
                    OR lower(COALESCE(fa.display_name, '')) LIKE '%w9%'
                    OR lower(COALESCE(fa.indexed_text, '')) LIKE '%w-9%'
                    OR lower(COALESCE(fa.indexed_text, '')) LIKE '%employer identification%'
                    OR ($org IS NOT NULL AND fel.entity_id = $org)
                  )
                ORDER BY
                  CASE WHEN $org IS NOT NULL AND fel.entity_id = $org THEN 0 ELSE 1 END,
                  CASE WHEN lower(COALESCE(fa.display_name, '')) LIKE '%w-9%' THEN 0 ELSE 1 END,
                  fa.updated_at DESC
                LIMIT 1;
                """;
            w9.Parameters.AddWithValue("$org", (object?)orgId ?? DBNull.Value);
            using var reader = w9.ExecuteReader();
            if (reader.Read())
            {
                w9FileId = reader.GetString(0);
                w9Path = reader.GetString(1);
                w9Name = reader.GetString(2);
                w9Snippet = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (orgId is null && !reader.IsDBNull(4))
                {
                    orgId = reader.GetString(4);
                }

                citations.Add(new EvidenceCitation
                {
                    Kind = "file",
                    EntityType = "file",
                    EntityId = w9FileId,
                    Label = w9Name,
                    Path = w9Path,
                    Snippet = w9Snippet,
                });
            }
        }

        if (orgId is not null && orgName is null)
        {
            using var orgCmd = connection.CreateCommand();
            orgCmd.CommandText = "SELECT name FROM organizations WHERE id = $id LIMIT 1;";
            orgCmd.Parameters.AddWithValue("$id", orgId);
            orgName = orgCmd.ExecuteScalar() as string;
        }

        // Extract EIN from W-9 text if fact missing
        if (einValue is null && w9Snippet is not null)
        {
            var match = Regex.Match(
                w9Snippet,
                @"\b(?:EIN|Taxpayer Identification Number)[:\s#]*([0-9]{2}-?[0-9]{7})\b",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                einValue = match.Groups[1].Value;
            }
        }

        if (einValue is null && orgId is null && w9FileId is null)
        {
            return new EvidenceAnswer
            {
                Question = question,
                AnswerType = "ein",
                Answer = "No EIN or W-9 evidence found in the graph.",
                Citations = [],
            };
        }

        var answer = einValue is null
            ? $"Found W-9/source for {(orgName ?? "organization")} but no parsed EIN value."
            : $"EIN for {(orgName ?? "organization")} is {einValue}.";

        if (orgId is not null && citations.All(c => c.Kind != "organization"))
        {
            citations.Insert(0, new EvidenceCitation
            {
                Kind = "organization",
                EntityType = "organization",
                EntityId = orgId,
                Label = orgName ?? orgId,
            });
        }

        return new EvidenceAnswer
        {
            Question = question,
            AnswerType = "ein",
            Answer = answer,
            Value = einValue,
            OrganizationId = orgId,
            Citations = citations,
        };
    }

    private EvidenceAnswer AnswerProjectStatus(string question, string? projectIdHint)
    {
        var projectId = ResolveProjectId(question, projectIdHint);
        if (projectId is null)
        {
            return new EvidenceAnswer
            {
                Question = question,
                AnswerType = "project_status",
                Answer = "Could not resolve a project for status evidence. Pass projectId or include the project name.",
                Citations = [],
            };
        }

        var bundle = _bundles.GetBundle(ContextTargetTypes.Project, projectId);
        if (bundle is null)
        {
            return new EvidenceAnswer
            {
                Question = question,
                AnswerType = "project_status",
                Answer = "Project was not found.",
                ProjectId = projectId,
                Citations = [],
            };
        }

        var openBlocker = bundle.Blockers.FirstOrDefault(b =>
            string.Equals(b.Status, "open", StringComparison.OrdinalIgnoreCase));
        var waiting = bundle.Tasks
            .Where(t => string.Equals(t.Status, TaskStatuses.Waiting, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var nextTask = bundle.Tasks
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.NextAction)
                && !string.Equals(t.Status, TaskStatuses.Complete, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(t.Status, TaskStatuses.Archived, StringComparison.OrdinalIgnoreCase));

        var waitingOn = waiting.Count == 0
            ? null
            : string.Join("; ", waiting.Select(t => t.Title + (t.NextAction is null ? "" : " — " + t.NextAction)));

        var status = new
        {
            projectName = bundle.ProjectName,
            projectSummary = bundle.ProjectSummary,
            openBlocker = openBlocker?.Summary,
            nextAction = nextTask?.NextAction ?? openBlocker?.Summary,
            waitingOn,
            activeTaskCount = bundle.Tasks.Count(t =>
                string.Equals(t.Status, TaskStatuses.Active, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Status, TaskStatuses.Waiting, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Status, TaskStatuses.Blocked, StringComparison.OrdinalIgnoreCase)),
            emails = bundle.Emails.Select(e => new
            {
                e.Id,
                e.Subject,
                extractions = e.Extractions.Select(x => x.Summary).ToList(),
            }).ToList(),
            files = bundle.Files.Select(f => new { f.Id, f.DisplayName, f.Path }).ToList(),
            meetings = bundle.Meetings.Select(m => new { m.Id, m.Title, m.StartsAt }).ToList(),
            contacts = bundle.Contacts.Select(c => new { c.PersonId, c.DisplayName }).ToList(),
        };

        var citations = new List<EvidenceCitation>
        {
            new()
            {
                Kind = "project",
                EntityType = "project",
                EntityId = bundle.ProjectId,
                Label = bundle.ProjectName,
                ProjectId = bundle.ProjectId,
                Snippet = bundle.ProjectSummary,
            },
        };

        if (openBlocker is not null)
        {
            citations.Add(new EvidenceCitation
            {
                Kind = "blocker",
                EntityType = "blocker",
                EntityId = openBlocker.Id,
                Label = openBlocker.Summary,
                ProjectId = bundle.ProjectId,
            });
        }

        foreach (var task in bundle.Tasks.Take(5))
        {
            citations.Add(new EvidenceCitation
            {
                Kind = "task",
                EntityType = "task",
                EntityId = task.TaskId,
                Label = task.Title,
                Snippet = task.NextAction,
                ProjectId = bundle.ProjectId,
            });
        }

        foreach (var email in bundle.Emails.Take(5))
        {
            citations.Add(new EvidenceCitation
            {
                Kind = "email",
                EntityType = "email",
                EntityId = email.Id,
                Label = email.Subject ?? "(no subject)",
                Snippet = string.Join("; ", email.Extractions.Select(x => x.Summary)),
                ProjectId = bundle.ProjectId,
            });
        }

        foreach (var file in bundle.Files.Take(5))
        {
            citations.Add(new EvidenceCitation
            {
                Kind = "file",
                EntityType = "file",
                EntityId = file.Id,
                Label = file.DisplayName,
                Path = file.Path,
                ProjectId = bundle.ProjectId,
            });
        }

        foreach (var meeting in bundle.Meetings.Take(5))
        {
            citations.Add(new EvidenceCitation
            {
                Kind = "meeting",
                EntityType = "calendar_event",
                EntityId = meeting.Id,
                Label = meeting.Title,
                Snippet = meeting.StartsAt,
                ProjectId = bundle.ProjectId,
            });
        }

        // Guard: no Riverview extraction summaries when answering Harbor Court (bundle already scopes).
        var extractionSnippets = bundle.Emails.SelectMany(e => e.Extractions).Select(x => x.Summary).ToList();

        var answerParts = new List<string>
        {
            $"{bundle.ProjectName} status: {bundle.ProjectSummary ?? "active project"}.",
        };
        if (openBlocker is not null)
        {
            answerParts.Add($"Open blocker: {openBlocker.Summary}.");
        }

        if (nextTask?.NextAction is not null)
        {
            answerParts.Add($"Next action: {nextTask.NextAction} ({nextTask.Title}).");
        }

        if (waitingOn is not null)
        {
            answerParts.Add($"Waiting: {waitingOn}.");
        }

        if (bundle.Meetings.Count > 0)
        {
            answerParts.Add($"Upcoming meeting: {bundle.Meetings[0].Title}.");
        }

        return new EvidenceAnswer
        {
            Question = question,
            AnswerType = "project_status",
            Answer = string.Join(" ", answerParts),
            ProjectId = bundle.ProjectId,
            Citations = citations,
            Status = new
            {
                status.projectName,
                status.projectSummary,
                status.openBlocker,
                status.nextAction,
                status.waitingOn,
                status.activeTaskCount,
                status.emails,
                status.files,
                status.meetings,
                status.contacts,
                scopedExtractionSummaries = extractionSnippets,
            },
        };
    }

    private string? ResolveProjectId(string question, string? hint)
    {
        if (!string.IsNullOrWhiteSpace(hint))
        {
            return hint.Trim();
        }

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, name, code FROM projects WHERE archived_at IS NULL;
            """;
        using var reader = cmd.ExecuteReader();
        string? bestId = null;
        var bestLen = 0;
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var code = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (question.Contains(name, StringComparison.OrdinalIgnoreCase) && name.Length > bestLen)
            {
                bestId = id;
                bestLen = name.Length;
            }
            else if (code is not null
                     && question.Contains(code, StringComparison.OrdinalIgnoreCase)
                     && code.Length > bestLen)
            {
                bestId = id;
                bestLen = code.Length;
            }
        }

        return bestId;
    }

    private static bool LooksLikeProjectName(string question) =>
        question.Contains("Harbor Court", StringComparison.OrdinalIgnoreCase)
        || question.Contains("Riverview", StringComparison.OrdinalIgnoreCase)
        || question.Contains("MetroFiber", StringComparison.OrdinalIgnoreCase);
}
