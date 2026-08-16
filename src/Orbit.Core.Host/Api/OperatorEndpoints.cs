using System.Text.Json;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Pulse;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Core.Host.Api;

public static class OperatorEndpoints
{
    public static IEndpointRouteBuilder MapOperatorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.OperatorRuns, ListRuns);
        app.MapPost(HostEndpoints.OperatorRunsClearStuck, ClearStuckRuns);
        app.MapPost(HostEndpoints.OperatorWake, Wake);
        app.MapGet(HostEndpoints.OperatorRules, ListRules);
        app.MapPost(HostEndpoints.OperatorRules, CreateRule);
        app.MapPost($"{HostEndpoints.OperatorRules}/{{id}}/enable", EnableRule);
        app.MapPost($"{HostEndpoints.OperatorRules}/{{id}}/disable", DisableRule);
        app.MapPost($"{HostEndpoints.OperatorRules}/{{id}}/archive", ArchiveRule);
        app.MapGet(HostEndpoints.OperatorMemory, ListMemory);
        app.MapPost(HostEndpoints.OperatorMemoryRemember, Remember);
        app.MapPost($"{HostEndpoints.OperatorMemory}/{{id}}/forget", Forget);

        app.MapPost(HostEndpoints.AgentToolRemember, RememberTool);
        app.MapPost(HostEndpoints.AgentToolForget, ForgetTool);
        app.MapPost(HostEndpoints.AgentToolListRules, ListRulesTool);
        app.MapPost(HostEndpoints.AgentToolSetRule, SetRuleTool);
        app.MapPost(HostEndpoints.AgentToolListMemory, ListMemoryTool);
        app.MapPost(HostEndpoints.AgentToolReportBriefing, ReportBriefingTool);
        app.MapPost(HostEndpoints.AgentToolLinkEmailThread, LinkEmailThreadTool);
        app.MapPost(HostEndpoints.AgentToolListTaskEmails, ListTaskEmailsTool);
        app.MapPost(HostEndpoints.AgentToolOpenEmail, OpenEmailTool);
        app.MapGet(HostEndpoints.AgentToolListTaskEmails, ListTaskEmailsGet);
        app.MapPost($"{HostEndpoints.Suggestions}/{{id}}/always", AcceptAlways);
        return app;
    }

    private static IResult ListRuns(OperatorRunStore runs, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            runs = runs.ListRecent(30).Select(MapRun),
            requestId,
        });
    }

    private static IResult ClearStuckRuns(OperatorRunStore runs, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var abandoned = runs.AbandonAllRunning(
            "Cleared stuck operator runs (manual / banner recovery).");
        return Results.Json(new { ok = true, abandoned, requestId });
    }

    private static IResult Wake(OperatorWakeRequest? body, OperatorWakeService wake, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var trigger = string.IsNullOrWhiteSpace(body?.TriggerKind)
            ? OperatorTriggers.EmailIngested
            : body!.TriggerKind!.Trim();
        string? payload = body?.PayloadJson;
        if (string.IsNullOrWhiteSpace(payload) && body?.EmailId is not null)
        {
            payload = JsonSerializer.Serialize(new
            {
                type = "email.ingested",
                payload = new { emailId = body.EmailId, subject = body.Subject },
            });
        }

        wake.RequestWake(trigger, payload);
        return Results.Json(new { queued = true, triggerKind = trigger, requestId });
    }

    private static IResult ListRules(StandingRulesStore rules, bool? enabledOnly, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            rules = rules.List(enabledOnly == true).Select(MapRule),
            requestId,
        });
    }

    private static IResult CreateRule(CreateOperatorRuleRequest? body, StandingRulesStore rules, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var created = rules.Create(body);
            return Results.Json(new { rule = MapRule(created), requestId });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult EnableRule(string id, StandingRulesStore rules, HttpContext http) =>
        SetRuleEnabled(id, true, rules, http);

    private static IResult DisableRule(string id, StandingRulesStore rules, HttpContext http) =>
        SetRuleEnabled(id, false, rules, http);

    private static IResult SetRuleEnabled(string id, bool enabled, StandingRulesStore rules, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var updated = rules.SetEnabled(id, enabled);
        if (updated is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Rule was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { rule = MapRule(updated), requestId });
    }

    private static IResult ArchiveRule(string id, StandingRulesStore rules, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (!rules.Archive(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Rule was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { ok = true, requestId });
    }

    private static IResult ListMemory(OperatorMemoryStore memory, string? scope, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            memory = memory.List(scope).Select(MapMemory),
            requestId,
        });
    }

    private static IResult Remember(RememberRequest? body, OperatorMemoryStore memory, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var record = memory.Remember(body);
            return Results.Json(new { fact = MapMemory(record), requestId });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Forget(string id, OperatorMemoryStore memory, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (!memory.Forget(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Memory fact was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new { ok = true, requestId });
    }

    private static IResult RememberTool(RememberRequest? body, OperatorMemoryStore memory, HttpContext http) =>
        Remember(body, memory, http);

    private static IResult ReportBriefingTool(
        ReportBriefingBody? body,
        OperatorRunStore runs,
        PulseReadStore pulse,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null || string.IsNullOrWhiteSpace(body.Briefing))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "briefing is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var trigger = string.IsNullOrWhiteSpace(body.TriggerKind)
            ? OperatorTriggers.DutyScan
            : body.TriggerKind.Trim();
        if (!OperatorTriggers.All.Contains(trigger))
        {
            // Allow Hermes cron/webhook labels without expanding the standing-rules enum forever.
            if (trigger.Length > 64)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "triggerKind is too long.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var briefing = body.Briefing.Trim();
        var silent = string.Equals(briefing, "[SILENT]", StringComparison.OrdinalIgnoreCase);
        if (silent)
        {
            briefing = "Nothing material to surface.";
        }

        if (briefing.Length > 8000)
        {
            briefing = briefing[..8000];
        }

        var payloadJson = JsonSerializer.Serialize(new
        {
            source = "hermes.report_briefing",
            trigger,
            silent,
            notedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        // Complete a matching running run when Hermes is finishing that trigger.
        // Do NOT let pulse.refresh / duty.scan steal an open email.ingested shell —
        // that left mail pushes "still organizing" while cron briefings looked fine.
        var recent = runs.ListRecent(30);
        OperatorRunRecord? running = null;
        if (trigger.Contains("email", StringComparison.OrdinalIgnoreCase))
        {
            running = recent.FirstOrDefault(r =>
                string.Equals(r.Status, OperatorRunStatuses.Running, StringComparison.OrdinalIgnoreCase)
                && r.TriggerKind.Contains("email", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            running = recent.FirstOrDefault(r =>
                string.Equals(r.Status, OperatorRunStatuses.Running, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.TriggerKind, trigger, StringComparison.OrdinalIgnoreCase));
        }

        var run = running ?? runs.Start(trigger, payloadJson);
        runs.Complete(run.Id, OperatorRunStatuses.Completed, briefingSummary: briefing);

        try
        {
            if (!silent)
            {
                pulse.SaveSnapshot(
                    briefing.Length > 2500 ? briefing[..2500] : briefing,
                    JsonSerializer.Serialize(new
                    {
                        source = "hermes.report_briefing",
                        runId = run.Id,
                        trigger,
                        savedAt = DateTimeOffset.UtcNow.ToString("O"),
                    }));
            }
        }
        catch
        {
            // Pulse snapshot is best-effort; operator_runs still holds the briefing.
        }

        hub.Publish(new OrbitEvent
        {
            Type = "operator.briefing",
            Payload = new { runId = run.Id, trigger, briefing, silent },
        });

        return Results.Json(new { ok = true, runId = run.Id, triggerKind = trigger, silent, requestId });
    }

    private sealed class ReportBriefingBody
    {
        public string? Briefing { get; set; }

        public string? TriggerKind { get; set; }
    }

    private static IResult ForgetTool(ToolIdBody? body, OperatorMemoryStore memory, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.Id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Forget(body.Id, memory, http);
    }

    private static IResult ListRulesTool(StandingRulesStore rules, HttpContext http) =>
        ListRules(rules, enabledOnly: true, http);

    private static IResult SetRuleTool(CreateOperatorRuleRequest? body, StandingRulesStore rules, HttpContext http) =>
        CreateRule(body, rules, http);

    private static IResult ListMemoryTool(MemoryScopeBody? body, OperatorMemoryStore memory, HttpContext http) =>
        ListMemory(memory, body?.Scope, http);

    private static IResult LinkEmailThreadTool(
        LinkEmailThreadToolBody? body,
        TaskEmailThreadStore threads,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null || string.IsNullOrWhiteSpace(body.TaskId) || string.IsNullOrWhiteSpace(body.ConversationId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "taskId and conversationId are required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var linked = threads.Link(body.TaskId, body.ConversationId, body.AnchorEmailId, body.Actor ?? "hermes");
            return Results.Json(new { thread = linked, requestId });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult ListTaskEmailsTool(TaskIdBody? body, TaskEmailThreadStore threads, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.TaskId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "taskId is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(new { threads = threads.ListForTask(body.TaskId), requestId });
    }

    private static IResult ListTaskEmailsGet(string? taskId, TaskEmailThreadStore threads, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query taskId is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Json(new { threads = threads.ListForTask(taskId), requestId });
    }

    private static IResult OpenEmailTool(EmailIdBody? body, EmailArtifactStore emails, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.EmailId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "emailId is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var record = emails.Get(body.EmailId);
        if (record is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Email was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        string? bodyText = null;
        if (!string.IsNullOrWhiteSpace(record.BodyTextPath) && File.Exists(record.BodyTextPath))
        {
            try
            {
                bodyText = File.ReadAllText(record.BodyTextPath);
                if (bodyText.Length > 6000)
                {
                    bodyText = bodyText[..6000] + "…";
                }
            }
            catch
            {
                bodyText = record.BodyPreview;
            }
        }

        return Results.Json(new
        {
            emailId = record.Id,
            subject = record.Subject,
            sentAt = record.SentAt,
            bodyPreview = record.BodyPreview,
            bodyText = bodyText ?? record.BodyPreview,
            conversationId = record.ConversationId,
            internetMessageId = record.InternetMessageId,
            projectIds = record.ProjectIds,
            participants = record.Participants.Select(p => new
            {
                p.Role,
                p.Address,
                p.DisplayName,
            }),
            attachmentNames = record.Attachments.Select(a => a.FileName),
            // Path is host-local; Hermes in Docker cannot open it — prefer bodyText above.
            rawPath = record.RawPath,
            requestId,
        });
    }

    private static IResult AcceptAlways(
        string id,
        AcceptAlwaysBody? body,
        SuggestionStore suggestions,
        StandingRulesStore rules,
        OperatorMemoryStore memory,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            var suggestion = suggestions.Get(id)
                ?? throw new ArgumentException("Suggestion was not found.", nameof(id));
            var accepted = suggestions.Accept(id, body?.Actor, applyProjectId: body?.ApplyProjectId);
            var rule = rules.Create(new CreateOperatorRuleRequest
            {
                Name = body?.RuleName ?? $"Always: {suggestion.Summary}",
                TriggerKind = MapTrigger(suggestion.SuggestionType),
                ActionKind = MapAction(suggestion.SuggestionType),
                MatchJson = BuildMatchJson(suggestion),
                ParamsJson = suggestion.PayloadJson,
                Enabled = true,
                RequireConfirm = false,
            });
            try
            {
                EmailRelationMemory.RememberAlways(memory, suggestion);
            }
            catch
            {
                // Training memory is best-effort — accept + rule already committed.
            }

            return Results.Json(new
            {
                suggestion = new
                {
                    accepted.Suggestion.Id,
                    accepted.Suggestion.Status,
                    accepted.AppliedProjectId,
                    accepted.CreatedTaskId,
                },
                rule = MapRule(rule),
                requestId,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static string MapTrigger(string suggestionType) => suggestionType switch
    {
        SuggestionTypes.MergeIntoTask or SuggestionTypes.DisambiguateEmailClaim => OperatorTriggers.EmailIngested,
        SuggestionTypes.AssignToProject or SuggestionTypes.ReviewLimbo => OperatorTriggers.NoteCreated,
        _ => OperatorTriggers.SuggestionAlways,
    };

    private static string MapAction(string suggestionType) => suggestionType switch
    {
        SuggestionTypes.MergeIntoTask => OperatorActions.UpdateTask,
        SuggestionTypes.AssignToProject => OperatorActions.CreateTask,
        SuggestionTypes.DisambiguateEmailClaim => OperatorActions.CreateNote,
        _ => OperatorActions.CreateNote,
    };

    private static string? BuildMatchJson(AgentSuggestionRecord suggestion)
    {
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(suggestion.ProjectId))
        {
            payload["projectId"] = suggestion.ProjectId;
        }

        payload["suggestionType"] = suggestion.SuggestionType;
        return JsonSerializer.Serialize(payload);
    }

    private static object MapRun(OperatorRunRecord r) => new
    {
        r.Id,
        r.TriggerKind,
        r.TriggerPayloadJson,
        r.Status,
        r.BriefingSummary,
        r.ErrorText,
        r.HermesSessionId,
        r.HermesRunId,
        r.CreatedAt,
        r.CompletedAt,
    };

    private static object MapRule(OperatorRuleRecord r) => new
    {
        r.Id,
        r.Name,
        r.Enabled,
        r.TriggerKind,
        r.ActionKind,
        r.MatchJson,
        r.ParamsJson,
        r.RequireConfirm,
        r.CreatedAt,
        r.UpdatedAt,
    };

    private static object MapMemory(OperatorMemoryRecord r) => new
    {
        r.Id,
        r.Scope,
        r.Kind,
        r.Text,
        r.Confidence,
        r.Source,
        r.UpdatedAt,
    };

    private sealed class ToolIdBody
    {
        public string? Id { get; set; }
    }

    private sealed class TaskIdBody
    {
        public string? TaskId { get; set; }
    }

    private sealed class EmailIdBody
    {
        public string? EmailId { get; set; }
    }

    private sealed class MemoryScopeBody
    {
        public string? Scope { get; set; }
    }

    private sealed class LinkEmailThreadToolBody
    {
        public string? TaskId { get; set; }

        public string? ConversationId { get; set; }

        public string? AnchorEmailId { get; set; }

        public string? Actor { get; set; }
    }

    private sealed class AcceptAlwaysBody
    {
        public string? Actor { get; set; }

        public string? RuleName { get; set; }

        public string? ApplyProjectId { get; set; }
    }
}

public sealed class OperatorWakeRequest
{
    public string? TriggerKind { get; set; }

    public string? PayloadJson { get; set; }

    public string? EmailId { get; set; }

    public string? Subject { get; set; }
}
