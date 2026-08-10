using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Email;

/// <summary>
/// After email ingest / duty wake: ensure a task exists under a linked project with
/// non-empty living brief (body) + next_action, and the email thread is linked.
/// Hermes may do richer work; this is the reliability floor (Work Jarvis).
/// </summary>
public sealed class EmailDutyEnsureService
{
    private readonly SqliteConnectionFactory _factory;
    private readonly EmailArtifactStore _emails;
    private readonly TaskEmailThreadStore _threads;
    private readonly OrbitMutationStore _mutations;

    public EmailDutyEnsureService(
        SqliteConnectionFactory factory,
        EmailArtifactStore emails,
        TaskEmailThreadStore threads,
        OrbitMutationStore mutations)
    {
        _factory = factory;
        _emails = emails;
        _threads = threads;
        _mutations = mutations;
    }

    public EmailDutyEnsureResult Ensure(string emailId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        var email = _emails.Get(emailId);
        if (email is null)
        {
            return new EmailDutyEnsureResult { Ok = false, Detail = "Email not found." };
        }

        var projectIds = email.ProjectIds.ToList();
        if (projectIds.Count == 0)
        {
            var matches = EmailProjectAutoLinker.MatchProjects(_factory, email.Subject, email.BodyPreview);
            foreach (var m in matches.Where(m => m.Confidence >= 0.72))
            {
                _emails.LinkToProject(emailId, m.ProjectId);
                projectIds.Add(m.ProjectId);
            }

            email = _emails.Get(emailId) ?? email;
            projectIds = email.ProjectIds.ToList();
        }

        if (projectIds.Count == 0)
        {
            return new EmailDutyEnsureResult
            {
                Ok = false,
                Detail = "No project match — left for Limbo/Hermes.",
                EmailId = emailId,
            };
        }

        var projectId = projectIds[0];
        var subject = string.IsNullOrWhiteSpace(email.Subject) ? "Email follow-up" : email.Subject.Trim();
        var brief = BuildBrief(email);
        var next = BuildNextAction(email);

        var existingTaskId = FindLinkedTaskId(emailId)
            ?? FindOpenTaskBySubject(projectId, subject);

        string taskId;
        if (!string.IsNullOrWhiteSpace(existingTaskId))
        {
            taskId = existingTaskId;
            var current = GetTaskFields(taskId);
            var needNext = string.IsNullOrWhiteSpace(current?.NextAction);
            var needBody = string.IsNullOrWhiteSpace(current?.Body);
            if (needNext || needBody)
            {
                _mutations.UpdateTask(
                    taskId,
                    title: null,
                    status: current?.Status is null or "not_started" ? TaskStatuses.Active : null,
                    nextAction: needNext ? next : null,
                    actor: "duty-ensure",
                    body: needBody ? brief : null);
            }
        }
        else
        {
            var created = _mutations.CreateTask(
                title: Truncate(subject, 120),
                projectId: projectId,
                status: TaskStatuses.Active,
                actor: "duty-ensure",
                nextAction: next,
                body: brief);
            taskId = created.Id;
        }

        var conversationId = email.ConversationId ?? email.InternetMessageId ?? email.Id;
        try
        {
            _threads.Link(taskId, conversationId, anchorEmailId: email.Id, actor: "duty-ensure");
        }
        catch
        {
            // already linked or task missing — best effort
        }

        return new EmailDutyEnsureResult
        {
            Ok = true,
            EmailId = emailId,
            TaskId = taskId,
            ProjectId = projectId,
            Detail = $"Ensured brief on task {taskId}.",
        };
    }

    private string? FindLinkedTaskId(string emailId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT tet.task_id
            FROM task_email_threads tet
            WHERE tet.anchor_email_id = $id
               OR tet.conversation_id IN (
                    SELECT COALESCE(conversation_id, internet_message_id, id)
                    FROM email_artifacts WHERE id = $id)
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        return cmd.ExecuteScalar() as string;
    }

    private string? FindOpenTaskBySubject(string projectId, string subject)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM tasks
            WHERE project_id = $p AND archived_at IS NULL
              AND status NOT IN ('complete', 'archived')
              AND (
                title = $title
                OR instr(lower(title), lower($token)) > 0
              )
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$title", Truncate(subject, 120));
        var token = subject.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Length >= 4) ?? subject;
        cmd.Parameters.AddWithValue("$token", Truncate(token, 40));
        return cmd.ExecuteScalar() as string;
    }

    private (string? NextAction, string? Body, string? Status)? GetTaskFields(string taskId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT next_action, body, status FROM tasks WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", taskId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static string BuildBrief(EmailArtifactRecord email)
    {
        var from = email.Participants.FirstOrDefault(p => p.Role == "from");
        var who = from?.DisplayName ?? from?.Address ?? "someone";
        var preview = string.IsNullOrWhiteSpace(email.BodyPreview)
            ? "(no preview)"
            : Truncate(email.BodyPreview.Replace("\r\n", " ").Replace('\n', ' '), 400);
        return $"Living brief (auto): Email from {who} — “{email.Subject ?? "(no subject)"}”. {preview}";
    }

    private static string BuildNextAction(EmailArtifactRecord email)
    {
        var subject = email.Subject ?? "this email";
        return Truncate($"Review and respond: {subject}", 160);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}

public sealed class EmailDutyEnsureResult
{
    public bool Ok { get; init; }

    public string? EmailId { get; init; }

    public string? TaskId { get; init; }

    public string? ProjectId { get; init; }

    public string? Detail { get; init; }
}
