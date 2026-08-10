using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
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
                if (body is null || (body.Name is null && body.Summary is null))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide name and/or summary.", requestId),
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var updated = projects.Update(id, body.Name, body.Summary);
                hub.Publish(new OrbitEvent
                {
                    Type = "project.updated",
                    Payload = new { projectId = id, name = updated.Name, summary = updated.Summary },
                });
                return Results.Json(new
                {
                    projectId = id,
                    name = updated.Name,
                    summary = updated.Summary,
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

        app.MapGet("/v1/projects/{id}/context", (string id, ProjectContextReadStore contexts, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var context = contexts.GetContext(id);
            if (context is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Project was not found.", requestId),
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(new
            {
                id = context.Id,
                name = context.Name,
                summary = context.Summary,
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
            });
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

        return app;
    }

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
    };

    private static object MapLimbo(LimboNoteRecord note) => new
    {
        id = note.Id,
        originalText = note.OriginalText,
        createdAt = note.CreatedAt,
        suggestionId = note.SuggestionId,
        suggestionSummary = note.SuggestionSummary,
    };
}

public sealed class ArchiveRequest
{
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? Actor { get; set; }
}
