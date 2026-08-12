using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Contacts;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;
using Orbit.Infrastructure.Hermes;
using Orbit.Infrastructure.Pulse;
using Orbit.Infrastructure.Search;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Core.Host.Api;

/// <summary>
/// Orbit tools Hermes can call with the Core Host API key.
/// Read tools plus typed mutations; no SQL or arbitrary filesystem access.
/// </summary>
public static class AgentToolEndpoints
{
    public static IEndpointRouteBuilder MapAgentToolEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.AgentToolGetProject, GetProject);
        app.MapPost(HostEndpoints.AgentToolGetProject, GetProjectPost);

        app.MapGet(HostEndpoints.AgentToolGetContact, GetContact);
        app.MapPost(HostEndpoints.AgentToolGetContact, GetContactPost);
        app.MapPost(HostEndpoints.AgentToolUpdateContact, UpdateContact);
        app.MapPost(HostEndpoints.AgentToolListContacts, ListContactsTool);
        app.MapPost(HostEndpoints.AgentToolArchiveContact, ArchiveContactTool);
        app.MapPost(HostEndpoints.AgentToolFlagResident, FlagResidentTool);

        app.MapGet(HostEndpoints.AgentToolSearchFiles, SearchFiles);
        app.MapPost(HostEndpoints.AgentToolSearchFiles, SearchFilesPost);

        app.MapGet(HostEndpoints.AgentToolSearch, OrbitSearch);
        app.MapPost(HostEndpoints.AgentToolSearch, OrbitSearchPost);

        app.MapGet(HostEndpoints.AgentToolAnswerWithEvidence, AnswerWithEvidence);
        app.MapPost(HostEndpoints.AgentToolAnswerWithEvidence, AnswerWithEvidencePost);

        app.MapGet(HostEndpoints.AgentToolGetRelatedContext, GetRelatedContext);
        app.MapPost(HostEndpoints.AgentToolGetRelatedContext, GetRelatedContextPost);

        app.MapGet(HostEndpoints.AgentToolGetCalendarContext, GetCalendarContext);
        app.MapPost(HostEndpoints.AgentToolGetCalendarContext, GetCalendarContextPost);

        app.MapPost(HostEndpoints.AgentToolCreateTask, CreateTask);
        app.MapPost(HostEndpoints.AgentToolUpdateTask, UpdateTask);
        app.MapPost(HostEndpoints.AgentToolCreateProject, CreateProject);
        app.MapPost(HostEndpoints.AgentToolUpdateProject, UpdateProject);
        app.MapPost(HostEndpoints.AgentToolMergeProject, MergeProject);
        app.MapPost(HostEndpoints.AgentToolAddProjectAlias, AddProjectAlias);
        app.MapPost(HostEndpoints.AgentToolRemoveProjectAlias, RemoveProjectAlias);
        app.MapGet(HostEndpoints.AgentToolListProjectAliases, ListProjectAliases);
        app.MapPost(HostEndpoints.AgentToolListProjectAliases, ListProjectAliasesPost);
        app.MapPost(HostEndpoints.AgentToolCreateWorkstream, CreateWorkstream);
        app.MapGet(HostEndpoints.AgentToolListWorkstreams, ListWorkstreams);
        app.MapPost(HostEndpoints.AgentToolListWorkstreams, ListWorkstreamsPost);
        app.MapGet(HostEndpoints.AgentToolGetWorkbench, GetWorkbench);
        app.MapPost(HostEndpoints.AgentToolGetWorkbench, GetWorkbenchPost);
        app.MapPost(HostEndpoints.AgentToolCreateNote, CreateNote);
        app.MapPost(HostEndpoints.AgentToolLinkEntities, LinkEntities);
        app.MapPost(HostEndpoints.AgentToolAcceptSuggestion, AcceptSuggestion);
        app.MapPost(HostEndpoints.AgentToolSetBlocker, SetBlocker);
        app.MapPost(HostEndpoints.AgentToolArchiveEntity, ArchiveEntity);

        app.MapPost(HostEndpoints.AgentToolLinkTasks, LinkTasks);
        app.MapPost(HostEndpoints.AgentToolUnlinkTasks, UnlinkTasks);
        app.MapGet(HostEndpoints.AgentToolGetTaskDependencies, GetTaskDependencies);
        app.MapPost(HostEndpoints.AgentToolGetTaskDependencies, GetTaskDependenciesPost);
        app.MapPost(HostEndpoints.AgentToolSuggestTaskLinks, SuggestTaskLinks);
        app.MapPost(HostEndpoints.AgentToolRejectSuggestion, RejectSuggestion);

        app.MapPost(HostEndpoints.AgentToolGetChanges, GetChangesTool);
        app.MapPost(HostEndpoints.AgentToolGetPulseDelta, GetPulseDeltaTool);
        app.MapPost(HostEndpoints.AgentToolListBlockedTasks, ListBlockedTasksTool);
        app.MapPost(HostEndpoints.AgentToolGetAgentSnapshot, GetAgentSnapshotTool);
        app.MapPost(HostEndpoints.AgentToolHealth, HealthTool);

        return app;
    }

    private static IResult GetProject(string? id, ProjectReadStore projects, ProjectContextReadStore contexts, OrbitMutationStore mutations, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ProjectPayload(id, projects, contexts, requestId, mutations);
    }

    private static IResult GetProjectPost(ToolIdBody? body, ProjectReadStore projects, ProjectContextReadStore contexts, OrbitMutationStore mutations, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var id = body?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ProjectPayload(id, projects, contexts, requestId, mutations);
    }

    private static IResult ProjectPayload(
        string id,
        ProjectReadStore projects,
        ProjectContextReadStore contexts,
        string requestId,
        OrbitMutationStore? mutations = null)
    {
        var project = projects.Get(id);
        if (project is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Project was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        var context = contexts.GetContext(id);
        IReadOnlyList<MutationWorkstreamResult>? workstreams = null;
        try
        {
            workstreams = mutations?.ListWorkstreams(id);
        }
        catch (ArgumentException)
        {
            workstreams = null;
        }

        return Results.Json(new
        {
            tool = "orbit_get_project",
            requestId,
            project = new
            {
                project.Id,
                project.Name,
                project.Code,
                project.Summary,
                project.Status,
                project.AccentColor,
                dossier = project.Dossier is null ? null : WorkbenchEndpoints.MapDossier(project.Dossier),
                dossierEmpty = project.DossierEmpty,
            },
            workstreams,
            hierarchyHint = "Project → workstreams (sub-areas) → tasks. Use orbit_create_workstream for sub-areas; orbit_create_task with workstreamId to nest tasks.",
            context,
        });
    }

    private static IResult GetContact(string? id, ContactStore contacts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ContactPayload(id, contacts, requestId);
    }

    private static IResult GetContactPost(ToolIdBody? body, ContactStore contacts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var id = body?.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ContactPayload(id, contacts, requestId);
    }

    private static IResult ContactPayload(string id, ContactStore contacts, string requestId)
    {
        var person = contacts.GetPerson(id);
        if (person is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Contact was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new
        {
            tool = "orbit_get_contact",
            requestId,
            contact = person,
        });
    }

    private static IResult SearchFiles(string? q, string? projectId, FileIndexService index, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'q' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return SearchPayload(q, projectId, index, requestId);
    }

    private static IResult SearchFilesPost(SearchFilesBody? body, FileIndexService index, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var q = body?.Q ?? body?.Query;
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'q' (or 'query') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return SearchPayload(q, body?.ProjectId, index, requestId);
    }

    private static IResult SearchPayload(string q, string? projectId, FileIndexService index, string requestId)
    {
        var hits = index.Search(q.Trim(), projectId, limit: 40);
        return Results.Json(new
        {
            tool = "orbit_search_files",
            requestId,
            query = q.Trim(),
            projectId,
            results = hits.Select(h => new
            {
                h.Id,
                h.DisplayName,
                h.Path,
                h.Extension,
                h.Snippet,
                h.ProjectId,
            }),
        });
    }

    private static IResult OrbitSearch(
        string? q,
        string? focusProjectId,
        string? focusMeetingId,
        GlobalSearchService search,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'q' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return OrbitSearchPayload(q, focusProjectId, focusMeetingId, search, requestId);
    }

    private static IResult OrbitSearchPost(OrbitSearchBody? body, GlobalSearchService search, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var q = body?.Q ?? body?.Query;
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'q' (or 'query') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return OrbitSearchPayload(q, body?.FocusProjectId, body?.FocusMeetingId, search, requestId);
    }

    private static IResult OrbitSearchPayload(
        string q,
        string? focusProjectId,
        string? focusMeetingId,
        GlobalSearchService search,
        string requestId)
    {
        var hits = search.Search(q.Trim(), focusProjectId, focusMeetingId, limit: 40);
        return Results.Json(new
        {
            tool = "orbit_search",
            requestId,
            query = q.Trim(),
            focusProjectId,
            focusMeetingId,
            results = hits.Select(h => new
            {
                h.EntityType,
                h.EntityId,
                h.Title,
                h.Snippet,
                h.Score,
                h.ProjectId,
                h.Path,
                h.PreviewKind,
            }),
        });
    }

    private static IResult AnswerWithEvidence(
        string? q,
        string? question,
        string? projectId,
        EvidenceService evidence,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var text = q ?? question;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'q' (or 'question') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return EvidenceToolPayload(text, projectId, evidence, requestId);
    }

    private static IResult AnswerWithEvidencePost(EvidenceToolBody? body, EvidenceService evidence, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var text = body?.Question ?? body?.Q;
        if (string.IsNullOrWhiteSpace(text))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'question' (or 'q') is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return EvidenceToolPayload(text, body?.ProjectId, evidence, requestId);
    }

    private static IResult EvidenceToolPayload(string question, string? projectId, EvidenceService evidence, string requestId)
    {
        var answer = evidence.Query(question.Trim(), projectId);
        return Results.Json(new
        {
            tool = "orbit_answer_with_evidence",
            requestId,
            question = answer.Question,
            answerType = answer.AnswerType,
            answer = answer.Answer,
            value = answer.Value,
            projectId = answer.ProjectId,
            organizationId = answer.OrganizationId,
            citations = answer.Citations,
            status = answer.Status,
        });
    }

    private static IResult GetRelatedContext(
        string? targetType,
        string? targetId,
        string? attentionProjectId,
        ContextBundleService bundles,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
        {
            return Results.Json(
                ApiErrors.Create(
                    ApiErrorCodes.BadRequest,
                    "Query parameters 'targetType' and 'targetId' are required.",
                    requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return RelatedContextPayload(targetType, targetId, attentionProjectId, bundles, requestId);
    }

    private static IResult GetRelatedContextPost(RelatedContextBody? body, ContextBundleService bundles, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var targetType = body?.TargetType;
        var targetId = body?.TargetId;
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
        {
            return Results.Json(
                ApiErrors.Create(
                    ApiErrorCodes.BadRequest,
                    "Body fields 'targetType' and 'targetId' are required.",
                    requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return RelatedContextPayload(targetType, targetId, body?.AttentionProjectId, bundles, requestId);
    }

    private static IResult GetCalendarContext(
        int? days,
        int? limit,
        DateTimeOffset? changedSince,
        CalendarReadStore store,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return CalendarContextPayload(days, limit, changedSince, store, requestId);
    }

    private static IResult GetCalendarContextPost(CalendarContextBody? body, CalendarReadStore store, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return CalendarContextPayload(body?.Days, body?.Limit, body?.ChangedSince, store, requestId);
    }

    private static IResult CalendarContextPayload(
        int? days,
        int? limit,
        DateTimeOffset? changedSince,
        CalendarReadStore store,
        string requestId)
    {
        var windowDays = days is > 0 and <= 90 ? days.Value : 14;
        var take = limit is > 0 and <= 100 ? limit.Value : 40;
        var meetings = store.GetUpcomingContext(TimeSpan.FromDays(windowDays), take, changedSince);
        return Results.Json(new
        {
            tool = "orbit_get_calendar_context",
            requestId,
            windowDays,
            changedSince,
            meetings = meetings.Select(m => new
            {
                id = m.Id,
                title = m.Title,
                startsAt = m.StartsAt,
                endsAt = m.EndsAt,
                location = m.Location,
                attentionScore = m.AttentionScore,
                sourceId = m.SourceId,
                sourceName = m.SourceName,
                mailboxName = m.MailboxName,
                calendarName = m.CalendarName,
                organizer = m.Organizer,
                updatedAt = m.UpdatedAt,
                linkedEntities = m.LinkedEntities.Select(l => new
                {
                    entityType = l.EntityType,
                    entityId = l.EntityId,
                    label = l.Label,
                    confidence = l.Confidence,
                }),
            }),
        });
    }

    private static IResult RelatedContextPayload(
        string targetType,
        string targetId,
        string? attentionProjectId,
        ContextBundleService bundles,
        string requestId)
    {
        try
        {
            var bundle = bundles.GetBundle(targetType, targetId, attentionProjectId);
            if (bundle is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Context target was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            var dto = ContextBundleEndpoints.ToDto(bundle, requestId);
            return Results.Json(new
            {
                tool = "orbit_get_related_context",
                requestId,
                bundle = dto,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult CreateTask(CreateTaskBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Title) || string.IsNullOrWhiteSpace(body.ProjectId))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body fields 'title' and 'projectId' are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = mutations.CreateTask(
                body.Title,
                body.ProjectId,
                body.Status,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance),
                nextAction: body.NextAction,
                body: body.Body,
                workstreamId: body.WorkstreamId);
            hub.Publish(new OrbitEvent { Type = "task.created", Payload = new { taskId = result.Id, projectId = result.ProjectId } });
            return Results.Json(new { tool = "orbit_create_task", requestId, task = result }, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult UpdateTask(UpdateTaskBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = mutations.UpdateTask(
                body.Id,
                body.Title,
                body.Status,
                body.NextAction,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance),
                body.Body,
                body.DueAt,
                body.Priority,
                body.Urgency,
                body.ProjectId,
                body.WorkstreamId,
                clearWorkstream: body.ClearWorkstream == true);
            hub.Publish(new OrbitEvent { Type = "task.updated", Payload = new { taskId = result.Id, projectId = result.ProjectId } });
            return Results.Json(new { tool = "orbit_update_task", requestId, task = result });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult CreateProject(CreateProjectToolBody? body, ProjectWriteStore projects, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'name' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var conflicts = projects.FindCreateConflicts(body.Name);
            if (conflicts.Count > 0 && body.Force != true)
            {
                return Results.Json(
                    new
                    {
                        tool = "orbit_create_project",
                        requestId,
                        error = new
                        {
                            code = ApiErrorCodes.Conflict,
                            message = "A similar project already exists. Attach to an existing project or pass force=true after operator confirmation.",
                        },
                        candidates = conflicts.Select(c => new
                        {
                            projectId = c.ProjectId,
                            name = c.Name,
                            score = c.Score,
                            reason = c.Reason,
                        }),
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Hermes-created projects join the orbit home by default.
            var inOrbit = body.InOrbit ?? true;
            var created = projects.Create(body.Name, body.Summary, inOrbit: inOrbit, code: body.Code);
            if (body.Aliases is { Length: > 0 })
            {
                foreach (var alias in body.Aliases)
                {
                    if (!string.IsNullOrWhiteSpace(alias))
                    {
                        projects.AddAlias(created.Id, alias);
                    }
                }
            }

            hub.Publish(new OrbitEvent
            {
                Type = "project.created",
                Payload = new { projectId = created.Id, name = created.Name, inOrbit },
            });
            return Results.Json(
                new
                {
                    tool = "orbit_create_project",
                    requestId,
                    project = new
                    {
                        id = created.Id,
                        name = created.Name,
                        summary = created.Summary,
                        code = created.Code,
                        status = created.Status,
                        inOrbit,
                        createdAt = created.CreatedAt,
                        aliases = projects.ListAliases(created.Id).Select(a => new { id = a.Id, alias = a.Alias }),
                    },
                },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult UpdateProject(UpdateProjectToolBody? body, ProjectWriteStore projects, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var touchAccent = body.AccentColor is not null;
            var touchCode = body.Code is not null || body.ClearCode == true;
            var addAliases = body.AddAliases ?? [];
            var removeAliases = body.RemoveAliases ?? [];
            var touchDossier = body.Dossier?.HasAnyField == true;
            if (body.Name is null && body.Summary is null && !touchAccent && !touchCode
                && addAliases.Length == 0 && removeAliases.Length == 0 && !touchDossier)
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Provide at least one of name, summary, code, accentColor, dossier, addAliases, or removeAliases.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string? name = null;
            string? summary = null;
            string? code = null;
            if (body.Name is not null || body.Summary is not null || touchCode)
            {
                var codeValue = body.ClearCode == true ? null : body.Code;
                (name, summary, code) = projects.Update(body.Id, body.Name, body.Summary, codeValue, touchCode);
            }

            string? accentColor = null;
            var accentApplied = false;
            if (touchAccent)
            {
                accentColor = projects.SetAccentColor(body.Id, body.AccentColor);
                accentApplied = true;
            }

            ProjectDossier? dossier = null;
            if (touchDossier)
            {
                dossier = projects.UpdateDossier(body.Id, body.Dossier!);
            }

            var added = new List<object>();
            foreach (var alias in addAliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                var record = projects.AddAlias(body.Id, alias);
                added.Add(new { id = record.Id, alias = record.Alias });
            }

            var removed = new List<string>();
            foreach (var alias in removeAliases)
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    continue;
                }

                if (projects.RemoveAlias(body.Id, alias))
                {
                    removed.Add(alias.Trim());
                }
            }

            hub.Publish(new OrbitEvent
            {
                Type = "project.updated",
                Payload = new
                {
                    projectId = body.Id,
                    name,
                    summary,
                    code,
                    accentColor = accentApplied ? accentColor : null,
                    dossierUpdated = touchDossier,
                },
            });

            dossier ??= projects.GetDossier(body.Id);

            return Results.Json(new
            {
                tool = "orbit_update_project",
                requestId,
                projectId = body.Id,
                name,
                summary,
                code,
                accentColor = accentApplied ? accentColor : null,
                accentUpdated = accentApplied,
                dossier = WorkbenchEndpoints.MapDossier(dossier),
                dossierEmpty = dossier.IsStructurallyEmpty,
                aliasesAdded = added,
                aliasesRemoved = removed,
                aliases = projects.ListAliases(body.Id).Select(a => new { id = a.Id, alias = a.Alias }),
            });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult MergeProject(MergeProjectToolBody? body, ProjectMergeStore merge, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.SourceProjectId)
                || string.IsNullOrWhiteSpace(body.TargetProjectId))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'sourceProjectId' and 'targetProjectId' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (body.PreviewOnly == true)
            {
                var preview = merge.Preview(body.SourceProjectId, body.TargetProjectId);
                return Results.Json(new
                {
                    tool = "orbit_merge_project",
                    requestId,
                    preview = new
                    {
                        sourceProjectId = preview.SourceProjectId,
                        sourceName = preview.SourceName,
                        targetProjectId = preview.TargetProjectId,
                        targetName = preview.TargetName,
                        taskCount = preview.TaskCount,
                        noteCount = preview.NoteCount,
                        workstreamCount = preview.WorkstreamCount,
                        fileLinkCount = preview.FileLinkCount,
                        emailLinkCount = preview.EmailLinkCount,
                        contactLinkCount = preview.ContactLinkCount,
                        aliasCount = preview.AliasCount,
                        blockerCount = preview.BlockerCount,
                        folderCount = preview.FolderCount,
                        warnings = preview.Warnings,
                    },
                });
            }

            var result = merge.Merge(
                body.SourceProjectId,
                body.TargetProjectId,
                body.Force == true,
                body.Actor ?? "agent");
            hub.Publish(new OrbitEvent
            {
                Type = "project.merged",
                Payload = new
                {
                    sourceProjectId = result.SourceProjectId,
                    targetProjectId = result.TargetProjectId,
                },
            });
            return Results.Json(new
            {
                tool = "orbit_merge_project",
                requestId,
                merge = new
                {
                    sourceProjectId = result.SourceProjectId,
                    sourceName = result.SourceName,
                    targetProjectId = result.TargetProjectId,
                    targetName = result.TargetName,
                    archivedSource = result.ArchivedSource,
                    mergedAt = result.MergedAt,
                    moved = result.Moved,
                },
            });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult AddProjectAlias(ProjectAliasToolBody? body, ProjectWriteStore projects, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.Alias))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body fields 'projectId' and 'alias' are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var created = projects.AddAlias(body.ProjectId, body.Alias);
            hub.Publish(new OrbitEvent
            {
                Type = "project.alias_added",
                Payload = new { projectId = created.ProjectId, aliasId = created.Id, alias = created.Alias },
            });
            return Results.Json(
                new { tool = "orbit_add_project_alias", requestId, alias = created },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult RemoveProjectAlias(ProjectAliasToolBody? body, ProjectWriteStore projects, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ProjectId)
                || (string.IsNullOrWhiteSpace(body.Alias) && string.IsNullOrWhiteSpace(body.AliasId)))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'projectId' and 'alias' or 'aliasId' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var key = body.AliasId ?? body.Alias!;
            if (!projects.RemoveAlias(body.ProjectId, key))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Alias was not found on that project.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            hub.Publish(new OrbitEvent
            {
                Type = "project.alias_removed",
                Payload = new { projectId = body.ProjectId, alias = key },
            });
            return Results.Json(new { tool = "orbit_remove_project_alias", requestId, projectId = body.ProjectId, removed = key });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult ListProjectAliases(string? projectId, ProjectWriteStore projects, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'projectId' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ListProjectAliasesPayload(projectId, projects, requestId);
    }

    private static IResult ListProjectAliasesPost(ListProjectAliasesBody? body, ProjectWriteStore projects, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var projectId = body?.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'projectId' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ListProjectAliasesPayload(projectId, projects, requestId);
    }

    private static IResult ListProjectAliasesPayload(string projectId, ProjectWriteStore projects, string requestId)
    {
        try
        {
            var aliases = projects.ListAliases(projectId);
            return Results.Json(new
            {
                tool = "orbit_list_project_aliases",
                requestId,
                projectId,
                aliases = aliases.Select(a => new { id = a.Id, alias = a.Alias, normalizedAlias = a.NormalizedAlias }),
            });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult CreateWorkstream(CreateWorkstreamBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.ProjectId) || string.IsNullOrWhiteSpace(body.Name))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body fields 'projectId' and 'name' are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var created = mutations.CreateWorkstream(
                body.ProjectId,
                body.Name,
                body.NextAction,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));
            hub.Publish(new OrbitEvent
            {
                Type = "workstream.created",
                Payload = new { workstreamId = created.Id, projectId = created.ProjectId },
            });
            return Results.Json(
                new { tool = "orbit_create_workstream", requestId, workstream = created },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult ListWorkstreams(string? projectId, OrbitMutationStore mutations, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query parameter 'projectId' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ListWorkstreamsPayload(projectId, mutations, requestId);
    }

    private static IResult ListWorkstreamsPost(ListWorkstreamsBody? body, OrbitMutationStore mutations, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var projectId = body?.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'projectId' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        return ListWorkstreamsPayload(projectId, mutations, requestId);
    }

    private static IResult ListWorkstreamsPayload(string projectId, OrbitMutationStore mutations, string requestId)
    {
        try
        {
            var workstreams = mutations.ListWorkstreams(projectId);
            return Results.Json(new { tool = "orbit_list_workstreams", requestId, projectId, workstreams });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult GetWorkbench(string? projectId, WorkbenchReadStore workbench, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return WorkbenchPayload(projectId, workbench, requestId);
    }

    private static IResult GetWorkbenchPost(GetWorkbenchBody? body, WorkbenchReadStore workbench, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return WorkbenchPayload(body?.ProjectId, workbench, requestId);
    }

    private static IResult WorkbenchPayload(string? projectId, WorkbenchReadStore workbench, string requestId)
    {
        try
        {
            var snapshot = workbench.GetSnapshot(projectId);
            return Results.Json(new
            {
                tool = "orbit_get_workbench",
                requestId,
                scope = snapshot.Scope is null
                    ? null
                    : new
                    {
                        kind = snapshot.Scope.Kind,
                        projectId = snapshot.Scope.ProjectId,
                        projectName = snapshot.Scope.ProjectName,
                    },
                cells = snapshot.Cells.Select(c => new
                {
                    id = c.Id,
                    name = c.Name,
                    code = c.Code,
                    summary = c.Summary,
                    status = c.Status,
                    cellKind = c.CellKind,
                    accentColor = c.AccentColor,
                    openBlockerCount = c.OpenBlockerCount,
                    topBlockerSummary = c.TopBlockerSummary,
                    pendingSuggestionCount = c.PendingSuggestionCount,
                    lines = c.Lines.Select(l => new
                    {
                        taskId = l.TaskId,
                        title = l.Title,
                        status = l.Status,
                        nextAction = l.NextAction,
                    }),
                }),
                limboCount = snapshot.Limbo.Count,
            });
        }
        catch (ArgumentException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static IResult CreateNote(CreateNoteToolBody? body, NoteWriteStore notes, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Text))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'text' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = notes.CreateCapture(body.Text, string.IsNullOrWhiteSpace(body.ProjectId) ? null : body.ProjectId);
            hub.Publish(new OrbitEvent
            {
                Type = "note.created",
                Payload = new
                {
                    noteId = result.NoteId,
                    taskId = result.TaskId,
                    projectId = result.ProjectId,
                    isLimbo = result.IsLimbo,
                },
            });
            return Results.Json(new
            {
                tool = "orbit_create_note",
                requestId,
                noteId = result.NoteId,
                taskId = result.TaskId,
                projectId = result.ProjectId,
                isLimbo = result.IsLimbo,
                originalText = result.OriginalText,
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult LinkEntities(LinkEntitiesBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.SourceType)
                || string.IsNullOrWhiteSpace(body.SourceId)
                || string.IsNullOrWhiteSpace(body.TargetType)
                || string.IsNullOrWhiteSpace(body.TargetId)
                || string.IsNullOrWhiteSpace(body.RelationshipType))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "sourceType, sourceId, targetType, targetId, and relationshipType are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = mutations.LinkEntities(
                body.SourceType,
                body.SourceId,
                body.TargetType,
                body.TargetId,
                body.RelationshipType,
                body.ProjectId,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));
            hub.Publish(new OrbitEvent { Type = "entities.linked", Payload = new { relationshipId = result.Id } });
            return Results.Json(new { tool = "orbit_link_entities", requestId, link = result }, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult UpdateContact(UpdateContactToolBody? body, ContactStore contacts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Id) || body.Patch is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body fields 'id' and 'patch' are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var detail = contacts.UpdateContact(
                body.Id,
                body.Patch,
                body.Provenance,
                body.Actor ?? body.RequestedBy ?? "agent",
                MapProvenance(body.RequestProvenance));
            hub.Publish(new OrbitEvent
            {
                Type = "entity.changed",
                Payload = new { entityType = "person", entityId = detail.Id },
            });
            return Results.Json(new { tool = "orbit_update_contact", requestId, contact = detail });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult ListContactsTool(ListContactsToolBody? body, ContactStore contacts, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var list = contacts.ListPeople(body?.Category, body?.Disposition);
        var limit = body?.Limit is > 0 and <= 500 ? body.Limit.Value : 100;
        return Results.Json(new
        {
            tool = "orbit_list_contacts",
            requestId,
            contacts = list.Take(limit).Select(c => new
            {
                id = c.Id,
                displayName = c.DisplayName,
                title = c.Title,
                organizationName = c.OrganizationName,
                primaryEmail = c.PrimaryEmail,
                primaryPhone = c.PrimaryPhone,
                category = c.Category,
                disposition = c.Disposition,
            }),
        });
    }

    private static IResult ArchiveContactTool(ArchiveContactToolBody? body, ContactStore contacts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var exclude = body.ExcludeAsResident == true;
        var detail = contacts.ArchivePerson(body.Id, exclude, body.Provenance, body.Actor ?? "agent");
        if (detail is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Contact was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        hub.Publish(new OrbitEvent
        {
            Type = "contact.archived",
            Payload = new { contactId = body.Id, excludeAsResident = exclude },
        });
        return Results.Json(new
        {
            tool = "orbit_archive_contact",
            requestId,
            id = body.Id,
            excludeAsResident = exclude,
            disposition = detail.Disposition,
        });
    }

    private static IResult FlagResidentTool(ToolIdBody? body, ContactStore contacts, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var detail = contacts.UpdateContact(
                body.Id,
                new ContactPatch { Disposition = ContactDispositions.FlaggedResident, Category = string.Empty },
                provenance: "orbit_flag_resident",
                requestedBy: "agent");
            hub.Publish(new OrbitEvent
            {
                Type = "contact.flagged_resident",
                Payload = new { contactId = detail.Id },
            });
            return Results.Json(new { tool = "orbit_flag_resident", requestId, contact = detail });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private sealed class ListContactsToolBody
    {
        public string? Category { get; set; }

        public string? Disposition { get; set; }

        public int? Limit { get; set; }
    }

    private sealed class ArchiveContactToolBody
    {
        public string? Id { get; set; }

        public bool? ExcludeAsResident { get; set; }

        public string? Provenance { get; set; }

        public string? Actor { get; set; }
    }

    private static IResult AcceptSuggestion(AcceptSuggestionBody? body, SuggestionStore suggestions, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = suggestions.Accept(
                body.Id,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance),
                body.ApplyProjectId);
            hub.Publish(new OrbitEvent
            {
                Type = "suggestion.accepted",
                Payload = new
                {
                    suggestionId = result.Suggestion.Id,
                    noteId = result.AppliedNoteId,
                    projectId = result.AppliedProjectId,
                },
            });
            return Results.Json(new
            {
                tool = "orbit_accept_suggestion",
                requestId,
                suggestion = result.Suggestion,
                appliedNoteId = result.AppliedNoteId,
                appliedProjectId = result.AppliedProjectId,
                createdTaskId = result.CreatedTaskId,
            });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult SetBlocker(SetBlockerBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Summary))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'summary' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = mutations.SetBlocker(
                body.Summary,
                body.ProjectId,
                body.TaskId,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));
            hub.Publish(new OrbitEvent
            {
                Type = "blocker.created",
                Payload = new { blockerId = result.Id, projectId = result.ProjectId, taskId = result.TaskId },
            });
            return Results.Json(new { tool = "orbit_set_blocker", requestId, blocker = result }, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult ArchiveEntity(ArchiveEntityBody? body, OrbitMutationStore mutations, EventHub hub, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.EntityType) || string.IsNullOrWhiteSpace(body.EntityId))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body fields 'entityType' and 'entityId' are required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = mutations.Archive(body.EntityType, body.EntityId, body.Actor ?? "agent");
            hub.Publish(new OrbitEvent
            {
                Type = $"{result.EntityType}.archived",
                Payload = new { entityType = result.EntityType, entityId = result.EntityId },
            });
            return Results.Json(new { tool = "orbit_archive_entity", requestId, archived = result });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult LinkTasks(
        LinkTasksBody? body,
        TaskDependencyStore dependencies,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null
                || string.IsNullOrWhiteSpace(body.PredecessorTaskId)
                || string.IsNullOrWhiteSpace(body.SuccessorTaskId))
            {
                return Results.Json(
                    ApiErrors.Create(
                        ApiErrorCodes.BadRequest,
                        "Body fields 'predecessorTaskId' and 'successorTaskId' are required.",
                        requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = dependencies.Link(
                body.PredecessorTaskId,
                body.SuccessorTaskId,
                body.DependencyType,
                body.Reason,
                body.Expects,
                body.Confidence,
                body.EvidenceRef,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            hub.Publish(new OrbitEvent
            {
                Type = "task.dependency.linked",
                Payload = new
                {
                    dependencyId = result.Id,
                    predecessorTaskId = result.PredecessorTaskId,
                    successorTaskId = result.SuccessorTaskId,
                    dependencyType = result.DependencyType,
                },
            });

            return Results.Json(
                new { tool = "orbit_link_tasks", requestId, dependency = result },
                statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult UnlinkTasks(
        UnlinkTasksBody? body,
        TaskDependencyStore dependencies,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.DependencyId))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'dependencyId' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var removed = dependencies.Unlink(
                body.DependencyId,
                body.Actor ?? "agent",
                MapProvenance(body.Provenance));

            if (!removed)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Dependency was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            hub.Publish(new OrbitEvent
            {
                Type = "task.dependency.unlinked",
                Payload = new { dependencyId = body.DependencyId },
            });

            return Results.Json(new { tool = "orbit_unlink_tasks", requestId, removed = true });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
    }

    private static IResult GetTaskDependencies(string? taskId, TaskDependencyStore dependencies, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return TaskDependencyPayload(taskId, dependencies, requestId);
    }

    private static IResult GetTaskDependenciesPost(
        ToolIdBody? body,
        TaskDependencyStore dependencies,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return TaskDependencyPayload(body?.Id, dependencies, requestId);
    }

    private static IResult TaskDependencyPayload(
        string? taskId,
        TaskDependencyStore dependencies,
        string requestId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Query or body field 'id' (taskId) is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var edges = dependencies.ListForTask(taskId);
        return Results.Json(new
        {
            tool = "orbit_get_task_dependencies",
            requestId,
            taskId,
            waitingOn = edges
                .Where(e => e.AnchorIsSuccessor)
                .Select(MapEdge)
                .ToArray(),
            feeds = edges
                .Where(e => !e.AnchorIsSuccessor)
                .Select(MapEdge)
                .ToArray(),
        });
    }

    private static object MapEdge(TaskDependencyEdge edge) => new
    {
        dependencyId = edge.Dependency.Id,
        dependencyType = edge.Dependency.DependencyType,
        reason = edge.Dependency.Reason,
        expects = edge.Dependency.Expects,
        confidence = edge.Dependency.Confidence,
        evidenceRef = edge.Dependency.EvidenceRef,
        createdBy = edge.Dependency.CreatedBy,
        createdAt = edge.Dependency.CreatedAt,
        taskId = edge.OtherTaskId,
        title = edge.OtherTaskTitle,
        status = edge.OtherTaskStatus,
        nextAction = edge.OtherTaskNextAction,
        projectId = edge.OtherTaskProjectId,
        satisfied = edge.OtherTaskIsDone,
    };

    private static IResult SuggestTaskLinks(
        ToolIdBody? body,
        TaskRelationshipEngine engine,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' (taskId) is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var created = engine.SuggestLinksForTask(body.Id);
        return Results.Json(new
        {
            tool = "orbit_suggest_task_links",
            requestId,
            suggestions = created.Select(s => new
            {
                id = s.Id,
                suggestionType = s.SuggestionType,
                summary = s.Summary,
                payloadJson = s.PayloadJson,
                confidence = s.Confidence,
            }).ToArray(),
        });
    }

    private static IResult RejectSuggestion(
        AcceptSuggestionBody? body,
        SuggestionStore suggestions,
        EventHub hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        try
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Body field 'id' is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var result = suggestions.Reject(body.Id, body.Actor ?? "agent");
            hub.Publish(new OrbitEvent
            {
                Type = "suggestion.rejected",
                Payload = new { suggestionId = result.Id, suggestionType = result.SuggestionType },
            });
            return Results.Json(new { tool = "orbit_reject_suggestion", requestId, suggestion = result });
        }
        catch (ArgumentException ex)
        {
            return MutationError(ex, requestId);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult MutationError(ArgumentException ex, string requestId)
    {
        var notFound = ex.ParamName is "projectId" or "taskId" or "id" or "contactId" or "noteId";
        return Results.Json(
            ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
            statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
    }

    private static MutationProvenance? MapProvenance(MutationProvenanceBody? body)
    {
        if (body is null)
        {
            return null;
        }

        var mapped = new MutationProvenance
        {
            Actor = body.Actor,
            Channel = body.Channel,
            HermesSessionId = body.HermesSessionId,
            ExternalUserId = body.ExternalUserId,
            TelegramUserId = body.TelegramUserId,
        };
        return mapped.HasValues ? mapped : null;
    }

    private sealed class LinkTasksBody
    {
        public string? PredecessorTaskId { get; set; }

        public string? SuccessorTaskId { get; set; }

        public string? DependencyType { get; set; }

        public string? Reason { get; set; }

        public string? Expects { get; set; }

        public double? Confidence { get; set; }

        public string? EvidenceRef { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class UnlinkTasksBody
    {
        public string? DependencyId { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class MutationProvenanceBody
    {
        public string? Actor { get; set; }

        public string? Channel { get; set; }

        public string? HermesSessionId { get; set; }

        public string? ExternalUserId { get; set; }

        public string? TelegramUserId { get; set; }
    }

    private sealed class ToolIdBody
    {
        public string? Id { get; set; }
    }

    private sealed class SearchFilesBody
    {
        public string? Q { get; set; }

        public string? Query { get; set; }

        public string? ProjectId { get; set; }
    }

    private sealed class OrbitSearchBody
    {
        public string? Q { get; set; }

        public string? Query { get; set; }

        public string? FocusProjectId { get; set; }

        public string? FocusMeetingId { get; set; }
    }

    private sealed class EvidenceToolBody
    {
        public string? Question { get; set; }

        public string? Q { get; set; }

        public string? ProjectId { get; set; }
    }

    private sealed class RelatedContextBody
    {
        public string? TargetType { get; set; }

        public string? TargetId { get; set; }

        public string? AttentionProjectId { get; set; }
    }

    private sealed class CalendarContextBody
    {
        public int? Days { get; set; }

        public int? Limit { get; set; }

        public DateTimeOffset? ChangedSince { get; set; }
    }

    private sealed class CreateTaskBody
    {
        public string? Title { get; set; }

        public string? ProjectId { get; set; }

        public string? WorkstreamId { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Body { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class CreateProjectToolBody
    {
        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Code { get; set; }

        public string[]? Aliases { get; set; }

        /// <summary>Defaults to true for Hermes — project appears on Pulse orbit.</summary>
        public bool? InOrbit { get; set; }

        /// <summary>When true, create even if near-duplicate candidates exist.</summary>
        public bool? Force { get; set; }
    }

    private sealed class CreateWorkstreamBody
    {
        public string? ProjectId { get; set; }

        public string? Name { get; set; }

        public string? NextAction { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class ListWorkstreamsBody
    {
        public string? ProjectId { get; set; }
    }

    private sealed class ListProjectAliasesBody
    {
        public string? ProjectId { get; set; }
    }

    private sealed class ProjectAliasToolBody
    {
        public string? ProjectId { get; set; }

        public string? Alias { get; set; }

        public string? AliasId { get; set; }
    }

    private sealed class UpdateTaskBody
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Status { get; set; }

        public string? NextAction { get; set; }

        public string? Body { get; set; }

        public string? DueAt { get; set; }

        public int? Priority { get; set; }

        public int? Urgency { get; set; }

        public string? ProjectId { get; set; }

        public string? WorkstreamId { get; set; }

        public bool? ClearWorkstream { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class UpdateProjectToolBody
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? Code { get; set; }

        public bool? ClearCode { get; set; }

        public string[]? AddAliases { get; set; }

        public string[]? RemoveAliases { get; set; }

        /// <summary>#RRGGBB, named preset (blue/teal/...), or default/none/clear to restore theme.</summary>
        public string? AccentColor { get; set; }

        public ProjectDossierPatch? Dossier { get; set; }
    }

    private sealed class MergeProjectToolBody
    {
        public string? SourceProjectId { get; set; }

        public string? TargetProjectId { get; set; }

        /// <summary>When true, proceed despite preview warnings (e.g. dual home folders).</summary>
        public bool? Force { get; set; }

        /// <summary>When true, return counts only — do not merge.</summary>
        public bool? PreviewOnly { get; set; }

        public string? Actor { get; set; }
    }

    private sealed class GetWorkbenchBody
    {
        public string? ProjectId { get; set; }
    }

    private sealed class CreateNoteToolBody
    {
        public string? Text { get; set; }

        public string? ProjectId { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class LinkEntitiesBody
    {
        public string? SourceType { get; set; }

        public string? SourceId { get; set; }

        public string? TargetType { get; set; }

        public string? TargetId { get; set; }

        public string? RelationshipType { get; set; }

        public string? ProjectId { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class UpdateContactToolBody
    {
        public string? Id { get; set; }

        public ContactPatch? Patch { get; set; }

        /// <summary>Contact fact provenance string (Phase 8).</summary>
        public string? Provenance { get; set; }

        /// <summary>Platform/session audit provenance (Phase 13).</summary>
        public MutationProvenanceBody? RequestProvenance { get; set; }

        public string? RequestedBy { get; set; }

        public string? Actor { get; set; }
    }

    private sealed class AcceptSuggestionBody
    {
        public string? Id { get; set; }

        public string? Actor { get; set; }

        public string? ApplyProjectId { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class SetBlockerBody
    {
        public string? Summary { get; set; }

        public string? ProjectId { get; set; }

        public string? TaskId { get; set; }

        public string? Actor { get; set; }

        public MutationProvenanceBody? Provenance { get; set; }
    }

    private sealed class ArchiveEntityBody
    {
        public string? EntityType { get; set; }

        public string? EntityId { get; set; }

        public string? Actor { get; set; }
    }

    private sealed class CursorBody
    {
        public long? Cursor { get; set; }

        public int? Limit { get; set; }

        public string? ProjectId { get; set; }
    }

    private static IResult GetChangesTool(CursorBody? body, ChangeLogStore log, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var (events, next) = log.ListSince(body?.Cursor ?? 0, body?.Limit ?? 200);
        return Results.Json(new
        {
            tool = "orbit_get_changes",
            cursor = body?.Cursor ?? 0,
            nextCursor = next,
            events = events.Select(e => new
            {
                revision = e.Revision,
                entityType = e.EntityType,
                entityId = e.EntityId,
                changeKind = e.ChangeKind,
                sourceEvent = e.SourceEvent,
                tombstone = e.Tombstone,
            }),
            requestId,
        });
    }

    private static IResult GetPulseDeltaTool(CursorBody? body, ChangeLogStore log, PulseReadStore pulse, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var (events, next) = log.ListSince(body?.Cursor ?? 0, body?.Limit ?? 200);
        var concerns = pulse.GetPulse().Concerns
            .OrderBy(c => c.TaskId, StringComparer.Ordinal)
            .Select(c => new
            {
                taskId = c.TaskId,
                projectId = c.ProjectId,
                title = c.Title,
                status = c.Status,
                nextAction = c.NextAction,
            });
        return Results.Json(new
        {
            tool = "orbit_get_pulse_delta",
            cursor = body?.Cursor ?? 0,
            nextCursor = next,
            changed = events.Select(e => new { revision = e.Revision, entityType = e.EntityType, entityId = e.EntityId, sourceEvent = e.SourceEvent }),
            concerns,
            requestId,
        });
    }

    private static IResult ListBlockedTasksTool(CursorBody? body, SqliteConnectionFactory factory, HttpContext http)
    {
        // Reuse public GET shape via minimal query — same as AgentMonitorEndpoints.
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT t.id, t.project_id, p.name, t.title, t.status, t.next_action
            FROM tasks t
            JOIN projects p ON p.id = t.project_id
            WHERE t.status = 'blocked' AND t.archived_at IS NULL
              AND ($projectId IS NULL OR t.project_id = $projectId)
            ORDER BY t.id ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$projectId", string.IsNullOrWhiteSpace(body?.ProjectId) ? DBNull.Value : body!.ProjectId!);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(body?.Limit ?? 100, 1, 300));
        var rows = new List<object>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new
            {
                taskId = reader.GetString(0),
                projectId = reader.GetString(1),
                projectName = reader.GetString(2),
                title = reader.GetString(3),
                status = reader.GetString(4),
                nextAction = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return Results.Json(new { tool = "orbit_list_blocked_tasks", tasks = rows, requestId });
    }

    private static IResult GetAgentSnapshotTool(
        SqliteConnectionFactory factory,
        CalendarReadStore calendar,
        ChangeLogStore log,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var cursor = log.CurrentCursor();
        using var connection = factory.CreateConnection();
        var projects = new List<object>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, status, COALESCE(in_orbit, 0) FROM projects WHERE archived_at IS NULL ORDER BY id ASC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(new { id = reader.GetString(0), name = reader.GetString(1), status = reader.GetString(2), inOrbit = reader.GetInt64(3) != 0 });
            }
        }

        var tasks = new List<object>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id, project_id, title, status, COALESCE(next_action, ''), COALESCE(priority, 0), COALESCE(urgency, -1)
                FROM tasks
                WHERE archived_at IS NULL AND status IN ('blocked','waiting','active','not_started')
                ORDER BY id ASC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new
                {
                    id = reader.GetString(0),
                    projectId = reader.GetString(1),
                    title = reader.GetString(2),
                    status = reader.GetString(3),
                    nextAction = reader.GetString(4),
                    priority = reader.GetInt64(5),
                    urgency = reader.GetInt64(6),
                });
            }
        }

        var meetings = calendar.ListHighAttention(limit: 8)
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .Select(m => new { id = m.Id, title = m.Title, attentionScore = m.AttentionScore });

        return Results.Json(new
        {
            tool = "orbit_get_agent_snapshot",
            schema = "orbit.agent.snapshot.v1",
            changeCursor = cursor,
            projects,
            tasks,
            meetings,
            requestId,
        });
    }

    private static IResult HealthTool(HostOptions options, ChangeLogStore log, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var webhookSecretPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "hermes-webhook-secret.txt");
        var webhookSecretPresent = File.Exists(webhookSecretPath)
            && new FileInfo(webhookSecretPath).Length > 0;

        return Results.Json(new
        {
            tool = "orbit_health",
            ok = true,
            schema = "orbit.health.v1",
            core = new
            {
                version = typeof(AgentToolEndpoints).Assembly.GetName().Version?.ToString(),
                changeCursor = log.CurrentCursor(),
                bindAddress = options.BindAddress,
            },
            hermes = new
            {
                baseUrlConfigured = !string.IsNullOrWhiteSpace(options.HermesBaseUrl),
                baseUrl = options.HermesBaseUrl,
                apiKeyConfigured = !string.IsNullOrWhiteSpace(options.HermesApiKey),
                webhookBaseUrl = options.HermesWebhookBaseUrl
                    ?? HermesWebhookClient.TryDeriveWebhookBase(options.HermesBaseUrl)?.ToString(),
                webhookSecretPresent,
            },
            mcp = new { readyHint = "Call orbit_get_workbench; if it succeeds MCP↔Core is healthy." },
            requestId,
        });
    }
}
