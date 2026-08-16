using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Workbench;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Data;

namespace Orbit.Core.Host.Api;

public sealed class CreateProjectRequest
{
    public string? Name { get; set; }

    public string? Summary { get; set; }
}

public sealed class CreateNoteRequest
{
    public string? Text { get; set; }

    public string? ProjectId { get; set; }
}

public sealed class SetProjectAccentRequest
{
    /// <summary>#RRGGBB or null/empty to clear (theme default).</summary>
    public string? AccentColor { get; set; }
}

public sealed class UpdateProjectRequest
{
    public string? Name { get; set; }

    public string? Summary { get; set; }

    public string? Code { get; set; }

    /// <summary>When true, <see cref="Code"/> is applied (null/empty clears).</summary>
    public bool? ClearCode { get; set; }

    /// <summary>Partial dossier patch (structured project context).</summary>
    public ProjectDossierPatch? Dossier { get; set; }
}

public sealed class AddProjectAliasRequest
{
    public string? Alias { get; set; }
}

public sealed class UpdateNoteRequest
{
    public string? Text { get; set; }
}

public sealed class AssignLimboNoteRequest
{
    public string? ProjectId { get; set; }
}

public sealed class SetWorkbenchCellLayoutRequest
{
    public string? CellKind { get; set; }

    public double? X { get; set; }

    public double? Y { get; set; }

    public double? Width { get; set; }

    public double? Height { get; set; }

    public int? SortOrder { get; set; }
}

public static class WorkbenchEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Workbench, (WorkbenchReadStore workbench, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var scopedId = http.Request.Query["projectId"].FirstOrDefault();
                var snapshot = workbench.GetSnapshot(scopedId);
                return Results.Json(new
                {
                    scope = snapshot.Scope is null
                        ? null
                        : new
                        {
                            kind = snapshot.Scope.Kind,
                            projectId = snapshot.Scope.ProjectId,
                            projectName = snapshot.Scope.ProjectName,
                        },
                    cells = snapshot.Cells.Select(MapCell),
                    limbo = snapshot.Limbo.Select(MapLimbo),
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }
        });

        app.MapPost(HostEndpoints.Projects, (
            CreateProjectRequest? body,
            ProjectWriteStore projects,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var created = projects.Create(body?.Name, body?.Summary);
                hub.Publish(new OrbitEvent
                {
                    Type = "project.created",
                    Payload = new { projectId = created.Id, name = created.Name },
                });
                return Results.Json(new
                {
                    id = created.Id,
                    name = created.Name,
                    summary = created.Summary,
                    status = created.Status,
                    createdAt = created.CreatedAt,
                    requestId,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPatch(HostEndpoints.ProjectAccent, (
            string id,
            SetProjectAccentRequest? body,
            ProjectWriteStore projects,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var color = projects.SetAccentColor(id, body?.AccentColor);
                hub.Publish(new OrbitEvent
                {
                    Type = "project.updated",
                    Payload = new { projectId = id, accentColor = color },
                });
                return Results.Json(new { projectId = id, accentColor = color, requestId });
            }
            catch (ArgumentException ex)
            {
                var code = ex.ParamName == "projectId" ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest;
                var status = code == ApiErrorCodes.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(ApiErrors.Create(code, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapPatch(HostEndpoints.ProjectById, (
            string id,
            UpdateProjectRequest? body,
            ProjectWriteStore projects,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var touchCode = body?.Code is not null || body?.ClearCode == true;
                var touchDossier = body?.Dossier?.HasAnyField == true;
                if (body is null || (body.Name is null && body.Summary is null && !touchCode && !touchDossier))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide name, summary, code, and/or dossier.", requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                string? name = null;
                string? summary = null;
                string? code = null;
                if (body.Name is not null || body.Summary is not null || touchCode)
                {
                    var codeValue = body.ClearCode == true ? null : body.Code;
                    (name, summary, code) = projects.Update(id, body.Name, body.Summary, codeValue, touchCode);
                }

                ProjectDossier? dossier = null;
                if (touchDossier)
                {
                    dossier = projects.UpdateDossier(id, body.Dossier!);
                }
                else
                {
                    try
                    {
                        dossier = projects.GetDossier(id);
                    }
                    catch (ArgumentException)
                    {
                        dossier = null;
                    }
                }

                hub.Publish(new OrbitEvent
                {
                    Type = "project.updated",
                    Payload = new { projectId = id, name, summary, code, dossierUpdated = touchDossier },
                });
                return Results.Json(new
                {
                    projectId = id,
                    name,
                    summary,
                    code,
                    dossier = dossier is null ? null : MapDossier(dossier),
                    dossierEmpty = dossier?.IsStructurallyEmpty ?? true,
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                var err = ex.ParamName == "projectId" ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest;
                var status = err == ApiErrorCodes.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(ApiErrors.Create(err, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapGet(HostEndpoints.ProjectAliases, (
            string id,
            ProjectWriteStore projects,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var aliases = projects.ListAliases(id);
                return Results.Json(new
                {
                    projectId = id,
                    aliases = aliases.Select(a => new { id = a.Id, alias = a.Alias, normalizedAlias = a.NormalizedAlias }),
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }
        });

        app.MapPost(HostEndpoints.ProjectAliases, (
            string id,
            AddProjectAliasRequest? body,
            ProjectWriteStore projects,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (body is null || string.IsNullOrWhiteSpace(body.Alias))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide alias.", requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var created = projects.AddAlias(id, body.Alias);
                hub.Publish(new OrbitEvent
                {
                    Type = "project.alias_added",
                    Payload = new { projectId = id, aliasId = created.Id, alias = created.Alias },
                });
                return Results.Json(new
                {
                    id = created.Id,
                    projectId = created.ProjectId,
                    alias = created.Alias,
                    normalizedAlias = created.NormalizedAlias,
                    requestId,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                var status = ex.ParamName == "projectId"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: status);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.Conflict, ex.Message, requestId),
                    statusCode: StatusCodes.Status409Conflict);
            }
        });

        app.MapDelete(HostEndpoints.ProjectAliasById, (
            string id,
            string aliasId,
            ProjectWriteStore projects,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (!projects.RemoveAlias(id, aliasId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.NotFound, "Alias was not found on that project.", requestId),
                        statusCode: StatusCodes.Status404NotFound);
                }

                hub.Publish(new OrbitEvent
                {
                    Type = "project.alias_removed",
                    Payload = new { projectId = id, aliasId },
                });
                return Results.Json(new { projectId = id, aliasId, removed = true, requestId });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, ex.Message, requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }
        });

        app.MapPatch(HostEndpoints.NoteById, (
            string id,
            UpdateNoteRequest? body,
            NoteWriteStore notes,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var text = notes.UpdateText(id, body?.Text ?? string.Empty);
                hub.Publish(new OrbitEvent
                {
                    Type = "note.updated",
                    Payload = new { noteId = id },
                });
                return Results.Json(new { noteId = id, text, requestId });
            }
            catch (ArgumentException ex)
            {
                var code = ex.ParamName == "noteId" ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest;
                var status = code == ApiErrorCodes.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(ApiErrors.Create(code, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapPost("/v1/notes/{id}/assign", (
            string id,
            AssignLimboNoteRequest? body,
            NoteWriteStore notes,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (string.IsNullOrWhiteSpace(body?.ProjectId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide projectId.", requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = notes.AssignLimboToProject(id, body.ProjectId);
                hub.Publish(new OrbitEvent
                {
                    Type = "note.assigned",
                    Payload = new
                    {
                        noteId = result.NoteId,
                        taskId = result.TaskId,
                        projectId = result.ProjectId,
                    },
                });
                if (result.TaskId is not null)
                {
                    hub.Publish(new OrbitEvent
                    {
                        Type = "task.created",
                        Payload = new
                        {
                            taskId = result.TaskId,
                            projectId = result.ProjectId,
                            noteId = result.NoteId,
                        },
                    });
                }

                return Results.Json(new
                {
                    noteId = result.NoteId,
                    taskId = result.TaskId,
                    originalText = result.OriginalText,
                    projectId = result.ProjectId,
                    isLimbo = result.IsLimbo,
                    createdAt = result.CreatedAt,
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                var status = ex.ParamName is "projectId" or "noteId"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                var code = status == StatusCodes.Status404NotFound
                    ? ApiErrorCodes.NotFound
                    : ApiErrorCodes.BadRequest;
                return Results.Json(ApiErrors.Create(code, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapPatch(HostEndpoints.WorkbenchCellLayout, (
            string id,
            SetWorkbenchCellLayoutRequest? body,
            WorkbenchLayoutStore layouts,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var kind = string.IsNullOrWhiteSpace(body?.CellKind) ? "project" : body!.CellKind!;
                var layout = layouts.SetLayout(
                    kind,
                    id,
                    body?.X ?? 0,
                    body?.Y ?? 0,
                    body?.Width ?? WorkbenchLayoutStore.MinWidth,
                    body?.Height ?? WorkbenchLayoutStore.MinHeight,
                    body?.SortOrder ?? 0);
                hub.Publish(new OrbitEvent
                {
                    Type = "workbench.layout",
                    Payload = new
                    {
                        cellKind = layout.EntityKind,
                        entityId = layout.EntityId,
                        x = layout.X,
                        y = layout.Y,
                        width = layout.Width,
                        height = layout.Height,
                    },
                });
                return Results.Json(new
                {
                    cellKind = layout.EntityKind,
                    entityId = layout.EntityId,
                    x = layout.X,
                    y = layout.Y,
                    width = layout.Width,
                    height = layout.Height,
                    sortOrder = layout.SortOrder,
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                var notFound = string.Equals(ex.ParamName, "entityId", StringComparison.Ordinal);
                return Results.Json(
                    ApiErrors.Create(
                        notFound ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest,
                        ex.Message,
                        requestId),
                    statusCode: notFound ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost(HostEndpoints.Notes, (CreateNoteRequest body, NoteWriteStore notes, EventHub hub, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var result = notes.CreateCapture(body.Text ?? string.Empty, string.IsNullOrWhiteSpace(body.ProjectId) ? null : body.ProjectId);
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

                if (result.TaskId is not null)
                {
                    hub.Publish(new OrbitEvent
                    {
                        Type = "task.created",
                        Payload = new
                        {
                            taskId = result.TaskId,
                            projectId = result.ProjectId,
                            noteId = result.NoteId,
                        },
                    });
                }

                return Results.Json(new
                {
                    noteId = result.NoteId,
                    taskId = result.TaskId,
                    originalText = result.OriginalText,
                    projectId = result.ProjectId,
                    isLimbo = result.IsLimbo,
                    createdAt = result.CreatedAt,
                    requestId,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                var status = ex.ParamName == "projectId"
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                    statusCode: status);
            }
        });

        app.MapGet(HostEndpoints.Notes, (WorkbenchReadStore workbench, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var limboOnly = string.Equals(http.Request.Query["limbo"], "true", StringComparison.OrdinalIgnoreCase);
            var snapshot = workbench.GetSnapshot();
            var notes = limboOnly
                ? snapshot.Limbo
                : snapshot.Limbo;
            return Results.Json(new
            {
                notes = notes.Select(MapLimbo),
                requestId,
            });
        });

        app.MapGet(HostEndpoints.ProjectContext, (
            string id,
            ProjectContextReadStore contexts,
            ProjectLivingBriefMaintainer livingBriefs,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var context = contexts.GetContext(id);
            if (context is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Project was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            // Baseline living brief on project open when summary/dossier are blank.
            if (ProjectLivingBriefSynthesizer.NeedsBaseline(context.Summary, context.DossierEmpty))
            {
                try
                {
                    var applied = livingBriefs.EnsureBaseline(id);
                    if (applied.Applied)
                    {
                        hub.Publish(new OrbitEvent
                        {
                            Type = "project.updated",
                            Payload = new
                            {
                                projectId = id,
                                summary = applied.Summary,
                                livingBrief = true,
                                summaryUpdated = applied.SummaryUpdated,
                                dossierUpdated = applied.DossierUpdated,
                            },
                        });
                        context = contexts.GetContext(id) ?? context;
                    }
                }
                catch (ArgumentException)
                {
                    // Best-effort — still return context.
                }
            }

            return Results.Json(MapProjectContext(context, requestId));
        });

        app.MapPost(HostEndpoints.ProjectBriefRefresh, (
            string id,
            ProjectLivingBriefMaintainer livingBriefs,
            ProjectContextReadStore contexts,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                var result = livingBriefs.Refresh(id);
                if (result.Applied)
                {
                    hub.Publish(new OrbitEvent
                    {
                        Type = "project.updated",
                        Payload = new
                        {
                            projectId = id,
                            summary = result.Summary,
                            livingBrief = true,
                            summaryUpdated = result.SummaryUpdated,
                            dossierUpdated = result.DossierUpdated,
                        },
                    });
                }

                var context = contexts.GetContext(id);
                if (context is null)
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.NotFound, "Project was not found.", requestId),
                        statusCode: StatusCodes.Status404NotFound);
                }

                return Results.Json(new
                {
                    projectId = id,
                    applied = result.Applied,
                    summaryUpdated = result.SummaryUpdated,
                    dossierUpdated = result.DossierUpdated,
                    skipReason = result.SkipReason,
                    summary = result.Summary ?? context.Summary,
                    dossier = (result.Dossier ?? context.Dossier) is null
                        ? null
                        : MapDossier(result.Dossier ?? context.Dossier!),
                    dossierEmpty = result.DossierEmpty,
                    context = MapProjectContext(context, requestId),
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                var code = ex.ParamName == "projectId" ? ApiErrorCodes.NotFound : ApiErrorCodes.BadRequest;
                var status = code == ApiErrorCodes.NotFound
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                return Results.Json(ApiErrors.Create(code, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapGet(HostEndpoints.TaskById, (string id, ProjectContextReadStore contexts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var task = contexts.GetTask(id);
            if (task is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Task was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new
            {
                taskId = task.TaskId,
                projectId = task.ProjectId,
                title = task.Title,
                status = task.Status,
                nextAction = task.NextAction,
                body = task.Body,
                dueAt = task.DueAt,
                priority = task.Priority,
                urgency = task.Urgency,
                sourceKind = task.SourceKind,
                sourceConfidence = task.SourceConfidence,
                sourceMatchReason = task.SourceMatchReason,
                waitingOnLabel = task.WaitingOnLabel,
                waitingOnPersonId = task.WaitingOnPersonId,
                waitingOnOrganizationId = task.WaitingOnOrganizationId,
                waitingFollowUpAt = task.WaitingFollowUpAt,
                waitingCadence = task.WaitingCadence,
                waitingSatisfiedAt = task.WaitingSatisfiedAt,
                waitingEvidenceRef = task.WaitingEvidenceRef,
                createdAt = task.CreatedAt,
                updatedAt = task.UpdatedAt,
                requestId,
            });
        });

        app.MapGet(HostEndpoints.TaskHistory, (string id, TaskHistoryReadStore history, HttpContext http, int? limit = null) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "task id is required.", requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var facts = history.ListFacts(id, limit ?? 120);
            if (facts.Count == 0)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Task was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            var lines = TaskTimelineMapper.Map(facts, limit: limit ?? TaskTimelineMapper.DefaultLimit);
            return Results.Json(new
            {
                taskId = id.Trim(),
                facts = facts.Select(f => new
                {
                    kind = f.Kind,
                    at = f.At,
                    summary = f.Summary,
                    detail = f.Detail,
                    statusLabel = f.StatusLabel,
                    sourceEvent = f.SourceEvent,
                    dedupeKey = f.DedupeKey,
                }),
                lines = lines.Select(l => new
                {
                    kind = l.Kind,
                    at = l.At,
                    whenLabel = l.WhenLabel,
                    text = l.Text,
                }),
                requestId,
            });
        });

        app.MapGet(HostEndpoints.LimboNoteById, (string id, ProjectContextReadStore contexts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var note = contexts.GetLimboNote(id);
            if (note is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Limbo note was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new
            {
                id = note.Id,
                originalText = note.OriginalText,
                createdAt = note.CreatedAt,
                suggestionId = note.SuggestionId,
                suggestionSummary = note.SuggestionSummary,
                requestId,
            });
        });

        app.MapPost(HostEndpoints.ArchiveEntity, (
            ArchiveRequest body,
            OrbitMutationStore mutations,
            EventHub hub,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (body is null || string.IsNullOrWhiteSpace(body.EntityType) || string.IsNullOrWhiteSpace(body.EntityId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "entityType and entityId are required.", requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = mutations.Archive(body.EntityType, body.EntityId, body.Actor ?? "user");
                hub.Publish(new OrbitEvent
                {
                    Type = $"{result.EntityType}.archived",
                    Payload = new { entityType = result.EntityType, entityId = result.EntityId },
                });
                return Results.Json(new { requestId, archived = result });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost(HostEndpoints.ProjectMergePreview, (
            MergeProjectRequest? body,
            ProjectMergeStore merge,
            HttpContext http) =>
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
                            "sourceProjectId and targetProjectId are required.",
                            requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var preview = merge.Preview(body.SourceProjectId, body.TargetProjectId);
                return Results.Json(new { requestId, preview = MapMergePreview(preview) });
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId),
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost(HostEndpoints.ProjectMerge, (
            MergeProjectRequest? body,
            ProjectMergeStore merge,
            EventHub hub,
            HttpContext http) =>
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
                            "sourceProjectId and targetProjectId are required.",
                            requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var result = merge.Merge(
                    body.SourceProjectId,
                    body.TargetProjectId,
                    body.Force == true,
                    body.Actor ?? "user");
                hub.Publish(new OrbitEvent
                {
                    Type = "project.merged",
                    Payload = new
                    {
                        sourceProjectId = result.SourceProjectId,
                        targetProjectId = result.TargetProjectId,
                    },
                });
                return Results.Json(new { requestId, merge = MapMergeResult(result) });
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
        });

        return app;
    }

    private static object MapMergePreview(ProjectMergePreview preview) => new
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
    };

    private static object MapMergeResult(ProjectMergeResult result) => new
    {
        sourceProjectId = result.SourceProjectId,
        sourceName = result.SourceName,
        targetProjectId = result.TargetProjectId,
        targetName = result.TargetName,
        archivedSource = result.ArchivedSource,
        mergedAt = result.MergedAt,
        moved = new
        {
            tasks = result.Moved.Tasks,
            notes = result.Moved.Notes,
            workstreams = result.Moved.Workstreams,
            blockers = result.Moved.Blockers,
            suggestions = result.Moved.Suggestions,
            extractions = result.Moved.Extractions,
            folders = result.Moved.Folders,
            fileLinks = result.Moved.FileLinks,
            emailLinks = result.Moved.EmailLinks,
            contactLinks = result.Moved.ContactLinks,
            aliases = result.Moved.Aliases,
            eventLinks = result.Moved.EventLinks,
            sourceNameAliasAdded = result.Moved.SourceNameAliasAdded,
        },
    };

    private static object MapCell(ProjectCellRecord cell) => new
    {
        id = cell.Id,
        name = cell.Name,
        code = cell.Code,
        summary = cell.Summary,
        status = cell.Status,
        cellKind = cell.CellKind,
        lines = cell.Lines.Select(l => new
        {
            taskId = l.TaskId,
            title = l.Title,
            status = l.Status,
            nextAction = l.NextAction,
            body = l.Body,
        }),
        openBlockerCount = cell.OpenBlockerCount,
        topBlockerSummary = cell.TopBlockerSummary,
        upcomingMeetingTitle = cell.UpcomingMeetingTitle,
        upcomingMeetingStartsAt = cell.UpcomingMeetingStartsAt,
        pendingSuggestionCount = cell.PendingSuggestionCount,
        recentActivityAt = cell.RecentActivityAt,
        accentColor = cell.AccentColor,
        sortOrder = cell.SortOrder,
        boardX = cell.BoardX,
        boardY = cell.BoardY,
        boardW = cell.BoardW,
        boardH = cell.BoardH,
        dossierEmpty = cell.DossierEmpty,
        missingNextAction = cell.MissingNextAction,
    };

    private static object MapLimbo(LimboNoteRecord note) => new
    {
        id = note.Id,
        originalText = note.OriginalText,
        createdAt = note.CreatedAt,
        suggestionId = note.SuggestionId,
        suggestionSummary = note.SuggestionSummary,
    };

    private static object MapProjectContext(ProjectContextRecord context, string requestId) => new
    {
        id = context.Id,
        name = context.Name,
        summary = context.Summary,
        code = context.Code,
        dossier = context.Dossier is null ? null : MapDossier(context.Dossier),
        dossierEmpty = context.DossierEmpty,
        aliases = context.Aliases.Select(a => new { id = a.Id, alias = a.Alias }),
        tasks = context.Tasks.Select(t => new
        {
            taskId = t.TaskId,
            title = t.Title,
            status = t.Status,
            nextAction = t.NextAction,
            body = t.Body,
            dueAt = t.DueAt,
            priority = t.Priority,
            urgency = t.Urgency,
            waitingOnLabel = t.WaitingOnLabel,
        }),
        completedTasks = context.CompletedTasks.Select(t => new
        {
            taskId = t.TaskId,
            title = t.Title,
            status = t.Status,
            nextAction = t.NextAction,
            body = t.Body,
            dueAt = t.DueAt,
            priority = t.Priority,
            urgency = t.Urgency,
            waitingOnLabel = t.WaitingOnLabel,
        }),
        notes = context.Notes.Select(n => new
        {
            id = n.Id,
            originalText = n.OriginalText,
            createdAt = n.CreatedAt,
        }),
        blockers = context.Blockers.Select(b => new
        {
            id = b.Id,
            summary = b.Summary,
            status = b.Status,
            taskId = b.TaskId,
            createdAt = b.CreatedAt,
        }),
        contacts = context.Contacts.Select(c => new
        {
            personId = c.PersonId,
            displayName = c.DisplayName,
            title = c.Title,
            organizationName = c.OrganizationName,
        }),
        meetings = context.Meetings.Select(m => new
        {
            id = m.Id,
            title = m.Title,
            startsAt = m.StartsAt,
        }),
        suggestions = context.Suggestions.Select(s => new
        {
            id = s.Id,
            summary = s.Summary,
            status = s.Status,
            noteId = s.NoteId,
        }),
        files = context.Files.Select(f => new
        {
            id = f.Id,
            displayName = f.DisplayName,
            path = f.Path,
            extension = f.Extension,
        }),
        requestId,
    };

    internal static object MapDossier(ProjectDossier dossier) => new
    {
        version = dossier.Version,
        address = dossier.Address,
        ownerClient = dossier.OwnerClient,
        phase = dossier.Phase,
        portfolio = dossier.Portfolio,
        linkedFolder = dossier.LinkedFolder,
        criticalContacts = dossier.CriticalContacts.Select(c => new
        {
            name = c.Name,
            role = c.Role,
            personId = c.PersonId,
            contact = c.Contact,
        }),
        mailboxSources = dossier.MailboxSources,
        calendarSources = dossier.CalendarSources,
        currentPriorities = dossier.CurrentPriorities,
        empty = dossier.IsStructurallyEmpty,
    };
}

public sealed class MergeProjectRequest
{
    public string? SourceProjectId { get; set; }

    public string? TargetProjectId { get; set; }

    public bool? Force { get; set; }

    public string? Actor { get; set; }
}

public sealed class ArchiveRequest
{
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? Actor { get; set; }
}
