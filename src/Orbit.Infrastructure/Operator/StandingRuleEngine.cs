using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;

namespace Orbit.Infrastructure.Operator;

/// <summary>
/// Auto-applies enabled standing rules that do not require confirm.
/// </summary>
public sealed class StandingRuleEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly StandingRulesStore _rules;
    private readonly OrbitMutationStore _mutations;
    private readonly TaskEmailThreadStore _threads;
    private readonly NoteWriteStore _notes;

    public StandingRuleEngine(
        StandingRulesStore rules,
        OrbitMutationStore mutations,
        TaskEmailThreadStore threads,
        NoteWriteStore notes)
    {
        _rules = rules;
        _mutations = mutations;
        _threads = threads;
        _notes = notes;
    }

    public IReadOnlyList<StandingRuleApplyResult> ApplyMatching(
        string triggerKind,
        OperatorMatchContext context)
    {
        var results = new List<StandingRuleApplyResult>();
        foreach (var rule in _rules.Match(triggerKind, context))
        {
            if (rule.RequireConfirm)
            {
                results.Add(new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = false,
                    SkippedConfirm = true,
                    Summary = $"Rule '{rule.Name}' requires confirm.",
                });
                continue;
            }

            try
            {
                results.Add(Apply(rule, context));
            }
            catch (Exception ex)
            {
                results.Add(new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = false,
                    Summary = ex.Message,
                });
            }
        }

        return results;
    }

    private StandingRuleApplyResult Apply(OperatorRuleRecord rule, OperatorMatchContext context)
    {
        var provenance = new MutationProvenance
        {
            Actor = CreatedByActors.Hermes,
            Channel = "standing_rule",
            HermesSessionId = rule.Id,
        };

        using var paramsDoc = string.IsNullOrWhiteSpace(rule.ParamsJson)
            ? JsonDocument.Parse("{}")
            : JsonDocument.Parse(rule.ParamsJson);
        var p = paramsDoc.RootElement;

        switch (rule.ActionKind)
        {
            case OperatorActions.CreateTask:
            {
                var title = ReadString(p, "title")
                            ?? ReadString(p, "titleTemplate")
                            ?? $"Follow up ({rule.Name})";
                title = title
                    .Replace("{subject}", context.Subject ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    .Trim();
                var projectId = ReadString(p, "projectId") ?? context.ProjectId
                    ?? throw new InvalidOperationException("create_task rule needs projectId.");
                var created = _mutations.CreateTask(title, projectId, status: null, actor: "standing_rule", provenance);
                TryLinkEmail(created.Id, context, p);
                return new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = true,
                    EntityId = created.Id,
                    Summary = $"Created task '{created.Title}' via standing rule.",
                };
            }
            case OperatorActions.UpdateTask:
            {
                var taskId = ReadString(p, "taskId") ?? context.TaskId
                    ?? throw new InvalidOperationException("update_task rule needs taskId.");
                var updated = _mutations.UpdateTask(
                    taskId,
                    title: ReadString(p, "title"),
                    status: ReadString(p, "status"),
                    nextAction: ReadString(p, "nextAction"),
                    actor: "standing_rule",
                    provenance: provenance,
                    body: ReadString(p, "body"));
                return new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = true,
                    EntityId = updated.Id,
                    Summary = $"Updated task via standing rule '{rule.Name}'.",
                };
            }
            case OperatorActions.SetBlocker:
            {
                var taskId = ReadString(p, "taskId") ?? context.TaskId
                    ?? throw new InvalidOperationException("set_blocker rule needs taskId.");
                var reason = ReadString(p, "reason") ?? $"Blocked by rule '{rule.Name}'";
                _mutations.UpdateTask(
                    taskId,
                    title: null,
                    status: TaskStatuses.Blocked,
                    nextAction: reason,
                    actor: "standing_rule",
                    provenance: provenance);
                return new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = true,
                    EntityId = taskId,
                    Summary = $"Set blocker via standing rule '{rule.Name}'.",
                };
            }
            case OperatorActions.LinkEmailThread:
            {
                var taskId = ReadString(p, "taskId") ?? context.TaskId
                    ?? throw new InvalidOperationException("link_email_thread needs taskId.");
                if (!TryLinkEmail(taskId, context, p))
                {
                    throw new InvalidOperationException("link_email_thread needs emailId/conversationId.");
                }

                return new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = true,
                    EntityId = taskId,
                    Summary = $"Linked email thread via standing rule '{rule.Name}'.",
                };
            }
            case OperatorActions.CreateNote:
            {
                var text = ReadString(p, "text") ?? context.Subject ?? rule.Name;
                var projectId = ReadString(p, "projectId") ?? context.ProjectId;
                var note = _notes.CreateCapture(text, projectId);
                return new StandingRuleApplyResult
                {
                    RuleId = rule.Id,
                    Applied = true,
                    EntityId = note.NoteId,
                    Summary = $"Created note via standing rule '{rule.Name}'.",
                };
            }
            default:
                throw new InvalidOperationException($"Unsupported action '{rule.ActionKind}'.");
        }
    }

    private bool TryLinkEmail(string taskId, OperatorMatchContext context, JsonElement p)
    {
        var emailId = ReadString(p, "emailId") ?? context.EmailId;
        var conversationId = ReadString(p, "conversationId");
        if (string.IsNullOrWhiteSpace(emailId) && string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        // Prefer explicit conversation; TaskEmailThreadStore.Link will resolve from email when needed.
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            conversationId = emailId;
        }

        _threads.Link(taskId, conversationId!, emailId, actor: "standing_rule");
        return true;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.GetString() : null;
}

public sealed class StandingRuleApplyResult
{
    public required string RuleId { get; init; }

    public bool Applied { get; init; }

    public bool SkippedConfirm { get; init; }

    public string? EntityId { get; init; }

    public required string Summary { get; init; }
}
