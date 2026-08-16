using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Suggestions;

/// <summary>
/// Heuristic task-relationship detection (no LLM):
/// proposes dependency links between contingent tasks, flags gating predecessors that have
/// been satisfied, and proposes merging inbound info (email) into the task waiting for it.
/// Everything it produces is a pending suggestion — it never mutates tasks directly.
/// </summary>
public sealed class TaskRelationshipEngine
{
    private const int MaxCandidateTasks = 60;
    private const int MaxSuggestionsPerRun = 5;

    /// <summary>Verbs that indicate the task produces information someone else needs.</summary>
    private static readonly string[] ProducerVerbs =
    [
        "ask", "confirm", "collect", "get", "gather", "request", "find", "check", "verify",
        "contact", "call", "email", "reach", "follow", "quote", "count", "audit", "inspect",
        "determine", "clarify", "measure", "review",
    ];

    /// <summary>Verbs that indicate the task consumes information to act.</summary>
    private static readonly string[] ConsumerVerbs =
    [
        "open", "order", "submit", "schedule", "install", "setup", "activate", "apply",
        "provision", "purchase", "buy", "pay", "sign", "file", "book", "start", "create",
        "register", "enroll", "close", "finalize", "complete",
    ];

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "that", "this", "have", "has", "will", "need",
        "needs", "needed", "into", "about", "then", "than", "them", "they", "there", "their",
        "what", "when", "where", "which", "would", "could", "should", "been", "being", "was",
        "were", "are", "our", "out", "all", "any", "you", "your", "his", "her", "its", "not",
        "but", "can", "get", "got", "new", "old", "one", "two", "please", "thanks", "regards",
        "task", "todo", "note", "also", "just", "more", "some", "over", "under", "before",
        "after", "next", "make", "made", "back", "still", "here",
        // Generic mail/property noise that caused PG&E → Comcast/CAA false merges.
        "email", "message", "sent", "received", "subject", "forward", "attached",
        "account", "service", "payment", "billing", "utility", "lease", "property",
        "building", "unit", "apartment", "address", "phone", "number", "update",
        "regarding", "re", "fw", "fwd", "dear", "hello", "team", "thanks",
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SuggestionStore _suggestions;

    public TaskRelationshipEngine(SqliteConnectionFactory factory, SuggestionStore suggestions)
    {
        _factory = factory;
        _suggestions = suggestions;
    }

    /// <summary>
    /// Looks for sibling tasks in the same project that appear contingent on the given task
    /// and proposes a dependency link for each strong pair.
    /// </summary>
    public IReadOnlyList<AgentSuggestionRecord> SuggestLinksForTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var anchor = LoadTask(taskId);
        if (anchor is null || anchor.IsDone)
        {
            return [];
        }

        var siblings = LoadOpenTasksForProject(anchor.ProjectId, taskId);
        if (siblings.Count == 0)
        {
            return [];
        }

        var linked = LoadLinkedTaskIds(taskId);
        var created = new List<AgentSuggestionRecord>();
        var anchorTokens = Tokenize(anchor.SearchText);
        var anchorRole = ClassifyRole(anchor.SearchText);

        foreach (var candidate in RankCandidates(anchor, anchorTokens, anchorRole, siblings, linked))
        {
            if (created.Count >= MaxSuggestionsPerRun)
            {
                break;
            }

            var suggestion = CreateLinkSuggestion(anchor, candidate);
            if (suggestion is not null)
            {
                created.Add(suggestion);
            }
        }

        return created;
    }

    /// <summary>
    /// Gating dependencies whose predecessor finished but whose successor is still open —
    /// proposes a "your blocker cleared, can this proceed?" confirmation.
    /// </summary>
    public IReadOnlyList<AgentSuggestionRecord> SuggestReadyDependencies()
    {
        var store = new TaskDependencyStore(_factory);
        var created = new List<AgentSuggestionRecord>();

        foreach (var row in store.ListReadyDependencies())
        {
            if (created.Count >= MaxSuggestionsPerRun)
            {
                break;
            }

            var successorId = row.Dependency.SuccessorTaskId;
            if (_suggestions.HasPendingForPayloadTokens(
                    SuggestionTypes.DependencyReady,
                    successorId,
                    row.Dependency.Id))
            {
                continue;
            }

            var expects = string.IsNullOrWhiteSpace(row.Dependency.Expects)
                ? "what it was waiting on"
                : row.Dependency.Expects!;
            var carried = FirstMeaningfulLine(row.PredecessorNextAction, row.PredecessorBody);
            var summary = carried is null
                ? $"“{Shorten(row.PredecessorTitle, 60)}” is done. Does “{Shorten(row.SuccessorTitle, 60)}” have {expects} now?"
                : $"“{Shorten(row.PredecessorTitle, 60)}” is done ({Shorten(carried, 70)}). Ready to move “{Shorten(row.SuccessorTitle, 60)}”?";

            var payload = JsonSerializer.Serialize(new
            {
                dependencyId = row.Dependency.Id,
                taskId = successorId,
                predecessorTaskId = row.Dependency.PredecessorTaskId,
                expects = row.Dependency.Expects,
                carriedInfo = carried,
            });

            created.Add(_suggestions.Create(new CreateSuggestionRequest
            {
                SuggestionType = SuggestionTypes.DependencyReady,
                Summary = summary,
                PayloadJson = payload,
                ProjectId = row.SuccessorProjectId,
                TaskId = successorId,
                GroupKey = SuggestionHygiene.DependencyReadyKey(successorId, row.Dependency.Id),
                Confidence = 0.6,
            }));
        }

        return created;
    }

    /// <summary>
    /// Matches an ingested email against open tasks (weighted toward what a dependency says the
    /// task is waiting for) and proposes merging the relevant excerpt into that task.
    /// </summary>
    public IReadOnlyList<AgentSuggestionRecord> SuggestMergesFromEmail(
        string emailId,
        string? bodyText = null,
        string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        var email = LoadEmail(emailId);
        if (email is null)
        {
            return [];
        }

        // Operator already chose the project at ingest — Host token merges are noise; Hermes may link later.
        if (email.HasOperatorChosenProject)
        {
            return [];
        }

        var body = string.IsNullOrWhiteSpace(bodyText) ? email.BodyText : bodyText;
        var subjectLine = string.IsNullOrWhiteSpace(subject) ? email.Subject : subject;
        var haystack = $"{subjectLine}\n{body}".Trim();
        if (haystack.Length < 12)
        {
            return [];
        }

        var emailTokens = Tokenize(haystack);
        if (emailTokens.Count == 0)
        {
            return [];
        }

        var created = new List<AgentSuggestionRecord>();
        foreach (var task in LoadOpenTasksForProjects(email.ProjectIds))
        {
            if (created.Count >= MaxSuggestionsPerRun)
            {
                break;
            }

            // Title + next_action only — task body sprawl caused cross-topic false positives.
            var target = new HashSet<string>(Tokenize(task.MergeSearchText), StringComparer.OrdinalIgnoreCase);
            var expectations = LoadExpectations(task.Id);
            foreach (var expectation in expectations)
            {
                foreach (var token in Tokenize(expectation))
                {
                    target.Add(token);
                }
            }

            var overlap = target.Intersect(emailTokens, StringComparer.OrdinalIgnoreCase).ToList();
            // Stricter than the old ≥2 bag-of-tokens gate.
            if (overlap.Count < 3)
            {
                continue;
            }

            var excerpt = BestExcerpt(haystack, overlap);
            if (excerpt is null)
            {
                continue;
            }

            // An expectation match is the strong signal; bare title overlap is weaker.
            var expectationHit = expectations.Any(e =>
                Tokenize(e).Any(t => emailTokens.Contains(t, StringComparer.OrdinalIgnoreCase)));
            if (!expectationHit && overlap.Count < 4)
            {
                continue;
            }

            var confidence = Math.Min(0.75, (expectationHit ? 0.55 : 0.4) + (0.04 * overlap.Count));

            if (_suggestions.HasPendingForPayloadTokens(SuggestionTypes.MergeIntoTask, task.Id, emailId)
                || _suggestions.WasDecidedForPayloadTokens(SuggestionTypes.MergeIntoTask, task.Id, emailId))
            {
                continue;
            }

            var payload = JsonSerializer.Serialize(new
            {
                taskId = task.Id,
                text = excerpt,
                field = "body",
                sourceType = "email",
                sourceId = emailId,
                matchedOn = overlap.Take(6).ToArray(),
            });

            var summary =
                $"Email “{Shorten(subjectLine ?? "(no subject)", 50)}” may answer “{Shorten(task.Title, 50)}”: {Shorten(excerpt, 90)}";

            created.Add(_suggestions.Create(new CreateSuggestionRequest
            {
                SuggestionType = SuggestionTypes.MergeIntoTask,
                Summary = summary,
                PayloadJson = payload,
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                GroupKey = SuggestionHygiene.MergeIntoTaskKey(task.Id, emailId),
                Confidence = confidence,
            }));
        }

        return created;
    }

    private AgentSuggestionRecord? CreateLinkSuggestion(TaskRow anchor, CandidateLink candidate)
    {
        var predecessor = candidate.AnchorIsPredecessor ? anchor : candidate.Task;
        var successor = candidate.AnchorIsPredecessor ? candidate.Task : anchor;

        if (_suggestions.HasPendingForPayloadTokens(SuggestionTypes.LinkTasks, predecessor.Id, successor.Id)
            || _suggestions.WasDecidedForPayloadTokens(SuggestionTypes.LinkTasks, predecessor.Id, successor.Id))
        {
            return null;
        }

        var expects = candidate.SharedTokens.FirstOrDefault();
        var reason = candidate.DependencyType == TaskDependencyTypes.Informs
            ? $"“{Shorten(predecessor.Title, 50)}” looks like it produces what “{Shorten(successor.Title, 50)}” needs"
            : $"Both mention {string.Join(", ", candidate.SharedTokens.Take(3))}";

        var payload = JsonSerializer.Serialize(new
        {
            predecessorTaskId = predecessor.Id,
            successorTaskId = successor.Id,
            dependencyType = candidate.DependencyType,
            reason,
            expects,
            sharedTokens = candidate.SharedTokens.Take(6).ToArray(),
        });

        var summary = candidate.DependencyType == TaskDependencyTypes.Informs
            ? $"Link these? “{Shorten(successor.Title, 55)}” likely needs {expects} from “{Shorten(predecessor.Title, 55)}”."
            : $"Link these related tasks? “{Shorten(predecessor.Title, 55)}” and “{Shorten(successor.Title, 55)}”.";

        return _suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.LinkTasks,
            Summary = summary,
            PayloadJson = payload,
            ProjectId = anchor.ProjectId,
            TaskId = successor.Id,
            GroupKey = SuggestionHygiene.LinkTasksKey(
                predecessor.Id,
                successor.Id,
                candidate.DependencyType),
            Confidence = candidate.Confidence,
        });
    }

    private static IEnumerable<CandidateLink> RankCandidates(
        TaskRow anchor,
        List<string> anchorTokens,
        TaskRole anchorRole,
        List<TaskRow> siblings,
        HashSet<string> alreadyLinked)
    {
        var results = new List<CandidateLink>();
        foreach (var sibling in siblings)
        {
            if (alreadyLinked.Contains(sibling.Id))
            {
                continue;
            }

            var shared = Tokenize(sibling.SearchText)
                .Intersect(anchorTokens, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (shared.Count == 0)
            {
                continue;
            }

            var siblingRole = ClassifyRole(sibling.SearchText);
            var complementary =
                (anchorRole == TaskRole.Producer && siblingRole == TaskRole.Consumer)
                || (anchorRole == TaskRole.Consumer && siblingRole == TaskRole.Producer);

            if (complementary)
            {
                results.Add(new CandidateLink
                {
                    Task = sibling,
                    DependencyType = TaskDependencyTypes.Informs,
                    AnchorIsPredecessor = anchorRole == TaskRole.Producer,
                    SharedTokens = shared,
                    Confidence = Math.Min(0.75, 0.5 + (0.05 * shared.Count)),
                });
                continue;
            }

            // No producer/consumer signal — only worth surfacing on strong topical overlap.
            if (shared.Count >= 3)
            {
                results.Add(new CandidateLink
                {
                    Task = sibling,
                    DependencyType = TaskDependencyTypes.Relates,
                    AnchorIsPredecessor = true,
                    SharedTokens = shared,
                    Confidence = 0.35,
                });
            }
        }

        return results.OrderByDescending(r => r.Confidence).ThenByDescending(r => r.SharedTokens.Count);
    }

    private static TaskRole ClassifyRole(string text)
    {
        var lower = text.ToLowerInvariant();
        var producer = ProducerVerbs.Any(v => ContainsWord(lower, v));
        var consumer = ConsumerVerbs.Any(v => ContainsWord(lower, v));
        return (producer, consumer) switch
        {
            (true, false) => TaskRole.Producer,
            (false, true) => TaskRole.Consumer,
            _ => TaskRole.Unknown,
        };
    }

    private static bool ContainsWord(string haystack, string word)
    {
        var index = haystack.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            var startOk = index == 0 || !char.IsLetter(haystack[index - 1]);
            var endIndex = index + word.Length;
            var endOk = endIndex >= haystack.Length || !char.IsLetter(haystack[endIndex]);
            if (startOk && endOk)
            {
                return true;
            }

            index = haystack.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static List<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (var raw in text.Split(
                     [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'', '/', '\\', '—', '-', '·'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length <= 3 || Stopwords.Contains(token) || !token.Any(char.IsLetter))
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

    /// <summary>Picks the sentence that best matches the task, preferring ones containing figures.</summary>
    private static string? BestExcerpt(string haystack, IReadOnlyCollection<string> matchTokens)
    {
        var sentences = haystack
            .Split(['\n', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length is >= 8 and <= 400)
            .ToList();
        if (sentences.Count == 0)
        {
            return null;
        }

        string? best = null;
        var bestScore = 0;
        foreach (var sentence in sentences)
        {
            var score = matchTokens.Count(t => sentence.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (score == 0)
            {
                continue;
            }

            // Answers to "how many / which / what" are usually the line carrying a number.
            if (sentence.Any(char.IsDigit))
            {
                score += 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = sentence;
            }
        }

        return best is null ? null : Shorten(best, 240);
    }

    private static string? FirstMeaningfulLine(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var line = candidate
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.Length >= 4);
            if (line is not null)
            {
                return line;
            }
        }

        return null;
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

    private TaskRow? LoadTask(string taskId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, title, status, next_action, body
            FROM tasks
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadTaskRow(reader) : null;
    }

    private List<TaskRow> LoadOpenTasksForProject(string projectId, string excludeTaskId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, project_id, title, status, next_action, body
            FROM tasks
            WHERE project_id = $project
              AND id <> $exclude
              AND archived_at IS NULL
              AND status NOT IN ($complete, $archived)
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$exclude", excludeTaskId);
        cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
        cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
        cmd.Parameters.AddWithValue("$limit", MaxCandidateTasks);
        return ReadTaskRows(cmd);
    }

    private List<TaskRow> LoadOpenTasksForProjects(IReadOnlyList<string> projectIds)
    {
        if (projectIds.Count == 0)
        {
            return [];
        }

        var rows = new List<TaskRow>();
        foreach (var projectId in projectIds.Distinct(StringComparer.Ordinal))
        {
            using var connection = _factory.CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT id, project_id, title, status, next_action, body
                FROM tasks
                WHERE project_id = $project
                  AND archived_at IS NULL
                  AND status NOT IN ($complete, $archived)
                ORDER BY updated_at DESC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$project", projectId);
            cmd.Parameters.AddWithValue("$complete", TaskStatuses.Complete);
            cmd.Parameters.AddWithValue("$archived", TaskStatuses.Archived);
            cmd.Parameters.AddWithValue("$limit", MaxCandidateTasks);
            rows.AddRange(ReadTaskRows(cmd));
        }

        return rows;
    }

    /// <summary>What this task is waiting on, per its incoming gating dependencies.</summary>
    private List<string> LoadExpectations(string taskId)
    {
        var expectations = new List<string>();
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT expects, reason FROM task_dependencies
            WHERE successor_task_id = $id AND dependency_type IN ($blocks, $informs);
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.Parameters.AddWithValue("$blocks", TaskDependencyTypes.Blocks);
        cmd.Parameters.AddWithValue("$informs", TaskDependencyTypes.Informs);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
            {
                expectations.Add(reader.GetString(0));
            }
            else if (!reader.IsDBNull(1))
            {
                expectations.Add(reader.GetString(1));
            }
        }

        return expectations;
    }

    private HashSet<string> LoadLinkedTaskIds(string taskId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT predecessor_task_id, successor_task_id FROM task_dependencies
            WHERE predecessor_task_id = $id OR successor_task_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
            ids.Add(reader.GetString(1));
        }

        return ids;
    }

    private EmailRow? LoadEmail(string emailId)
    {
        using var connection = _factory.CreateConnection();
        string? subject;
        string? preview;
        string? bodyPath;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT subject, body_preview, body_text_path
                FROM email_artifacts
                WHERE id = $id AND archived_at IS NULL
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$id", emailId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            subject = reader.IsDBNull(0) ? null : reader.GetString(0);
            preview = reader.IsDBNull(1) ? null : reader.GetString(1);
            bodyPath = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var projectIds = new List<string>();
        var hasOperatorChosen = false;
        using (var links = connection.CreateCommand())
        {
            links.CommandText =
                """
                SELECT project_id, lower(COALESCE(match_reason, ''))
                FROM email_project_links
                WHERE email_artifact_id = $id;
                """;
            links.Parameters.AddWithValue("$id", emailId);
            using var reader = links.ExecuteReader();
            while (reader.Read())
            {
                projectIds.Add(reader.GetString(0));
                var reason = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (reason is "explicit" or "operator")
                {
                    hasOperatorChosen = true;
                }
            }
        }

        var bodyText = preview;
        if (!string.IsNullOrWhiteSpace(bodyPath) && File.Exists(bodyPath))
        {
            try
            {
                bodyText = File.ReadAllText(bodyPath);
            }
            catch (IOException)
            {
                // Fall back to the stored preview.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to the stored preview.
            }
        }

        return new EmailRow
        {
            Subject = subject,
            BodyText = bodyText,
            ProjectIds = projectIds,
            HasOperatorChosenProject = hasOperatorChosen,
        };
    }

    private static List<TaskRow> ReadTaskRows(SqliteCommand cmd)
    {
        var rows = new List<TaskRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(ReadTaskRow(reader));
        }

        return rows;
    }

    private static TaskRow ReadTaskRow(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ProjectId = reader.GetString(1),
        Title = reader.GetString(2),
        Status = reader.GetString(3),
        NextAction = reader.IsDBNull(4) ? null : reader.GetString(4),
        Body = reader.IsDBNull(5) ? null : reader.GetString(5),
    };

    private enum TaskRole
    {
        Unknown,
        Producer,
        Consumer,
    }

    private sealed class TaskRow
    {
        public required string Id { get; init; }

        public required string ProjectId { get; init; }

        public required string Title { get; init; }

        public required string Status { get; init; }

        public string? NextAction { get; init; }

        public string? Body { get; init; }

        public string SearchText => $"{Title} {NextAction} {Body}".Trim();

        /// <summary>Tokens used for email→task merge heuristics (exclude sprawling body).</summary>
        public string MergeSearchText => $"{Title} {NextAction}".Trim();

        public bool IsDone =>
            string.Equals(Status, TaskStatuses.Complete, StringComparison.Ordinal)
            || string.Equals(Status, TaskStatuses.Archived, StringComparison.Ordinal);
    }

    private sealed class CandidateLink
    {
        public required TaskRow Task { get; init; }

        public required string DependencyType { get; init; }

        public required bool AnchorIsPredecessor { get; init; }

        public required List<string> SharedTokens { get; init; }

        public required double Confidence { get; init; }
    }

    private sealed class EmailRow
    {
        public string? Subject { get; init; }

        public string? BodyText { get; init; }

        public required List<string> ProjectIds { get; init; }

        public bool HasOperatorChosenProject { get; init; }
    }
}
