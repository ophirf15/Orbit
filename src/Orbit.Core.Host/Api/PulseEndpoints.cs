using System.Text.Json;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Pulse;

namespace Orbit.Core.Host.Api;

public sealed class OrbitIgnitionFromListRequest
{
    public IReadOnlyList<string>? Names { get; set; }
}

public sealed class OrbitIgnitionFromProjectsRootRequest
{
    public string? RootPath { get; set; }
}

public static class PulseEndpoints
{
    public static IEndpointRouteBuilder MapPulseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.PulseGet, GetPulse);
        app.MapPost(HostEndpoints.PulseRefresh, RefreshPulse);
        app.MapGet(HostEndpoints.ConcernById, GetConcernById);
        app.MapGet(HostEndpoints.OrbitGet, GetOrbit);
        app.MapPost(HostEndpoints.OrbitIgnitionFromList, IgnitionFromList);
        app.MapPost(HostEndpoints.OrbitIgnitionFromProjectsRoot, IgnitionFromProjectsRoot);
        app.MapPost(HostEndpoints.OrbitIgnitionConfirm, IgnitionConfirm);
        return app;
    }

    private static IResult GetPulse(
        PulseReadStore pulse,
        OperatorRunStore runs,
        CalendarReadStore calendar,
        ChangeLogStore changes,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var view = pulse.GetPulse();
        var briefing = BuildBriefing(view, pulse, calendar, changes);
        return Results.Json(new
        {
            pulse = MapPulse(view, runs.ListRecent(1).FirstOrDefault(), briefing),
            requestId,
        });
    }

    private static IResult RefreshPulse(
        PulseReadStore pulse,
        OperatorRunStore runs,
        CalendarReadStore calendar,
        ChangeLogStore changes,
        OperatorWakeService? wake,
        EventHub? hub,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var payload = JsonSerializer.Serialize(new { type = "pulse.refresh", source = "host" });

        if (wake is not null)
        {
            wake.RequestWake(OperatorTriggers.DutyScan, payload);
        }
        else if (hub is not null)
        {
            hub.Publish(new OrbitEvent { Type = "pulse.refresh", Payload = new { source = "host" } });
        }

        var view = pulse.GetPulse();
        var briefing = BuildBriefing(view, pulse, calendar, changes);
        return Results.Json(new
        {
            pulse = MapPulse(view, runs.ListRecent(1).FirstOrDefault(), briefing),
            refreshQueued = wake is not null || hub is not null,
            requestId,
        });
    }

    private static IResult GetConcernById(
        string id,
        ProjectContextReadStore contexts,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var task = contexts.GetTask(id);
        if (task is null)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.NotFound, "Concern was not found.", requestId),
                statusCode: StatusCodes.Status404NotFound);
        }

        return Results.Json(new
        {
            concern = new
            {
                taskId = task.TaskId,
                projectId = task.ProjectId,
                title = task.Title,
                status = task.Status,
                nextAction = task.NextAction,
                body = task.Body,
            },
            requestId,
        });
    }

    private static IResult GetOrbit(PulseReadStore pulse, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        return Results.Json(new
        {
            ignitionCompleted = pulse.IsIgnitionCompleted(),
            projects = pulse.ListOrbitProjects().Select(MapOrbitProject),
            requestId,
        });
    }

    private static IResult IgnitionFromList(
        OrbitIgnitionFromListRequest? body,
        OrbitIgnitionService ignition,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (body?.Names is null || body.Names.Count == 0)
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Request body 'names' must be a non-empty array.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var results = ignition.FromList(body.Names);
        return Results.Json(new
        {
            projects = results.Select(MapIgnitionProject),
            requestId,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult IgnitionFromProjectsRoot(
        OrbitIgnitionFromProjectsRootRequest? body,
        OrbitIgnitionService ignition,
        HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        if (string.IsNullOrWhiteSpace(body?.RootPath))
        {
            return Results.Json(
                ApiErrors.Create(ApiErrorCodes.BadRequest, "Request body 'rootPath' is required.", requestId),
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var results = ignition.FromProjectsRoot(body.RootPath);
            return Results.Json(new
            {
                rootPath = Path.GetFullPath(body.RootPath.Trim()),
                projects = results.Select(MapIgnitionProject),
                requestId,
            }, statusCode: StatusCodes.Status201Created);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
        }
        catch (IOException ex)
        {
            return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
        }
    }

    private static IResult IgnitionConfirm(OrbitIgnitionService ignition, HttpContext http)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var snapshot = ignition.Confirm();
        return Results.Json(new
        {
            ignitionCompleted = true,
            snapshot = new
            {
                id = snapshot.Id,
                dayBrief = snapshot.DayBrief,
                createdAt = snapshot.CreatedAt,
            },
            requestId,
        });
    }

    private static object MapPulse(PulseView pulse, OperatorRunRecord? lastRun, PulseBriefingStrip? briefing = null) => new
    {
        dayBrief = pulse.DayBrief,
        hermesHint = pulse.HermesHint,
        generatedAt = pulse.GeneratedAt,
        briefIsSynthetic = pulse.BriefIsSynthetic,
        concerns = pulse.Concerns.Select(c => new
        {
            taskId = c.TaskId,
            projectId = c.ProjectId,
            projectName = c.ProjectName,
            title = c.Title,
            status = c.Status,
            nextAction = c.NextAction,
            bodyExcerpt = c.BodyExcerpt,
            updatedAt = c.UpdatedAt,
            sourceKind = c.SourceKind,
            sourceConfidence = c.SourceConfidence,
            sourceMatchReason = c.SourceMatchReason,
        }),
        unmatchedMail = pulse.UnmatchedMail.Select(m => new
        {
            suggestionId = m.SuggestionId,
            summary = m.Summary,
            emailId = m.EmailId,
            subject = m.Subject,
            snippet = m.Snippet,
            confidence = m.Confidence,
            createdAt = m.CreatedAt,
        }),
        briefing = briefing is null
            ? null
            : new
            {
                upcomingMeetings = briefing.UpcomingMeetings.Select(m => new
                {
                    id = m.Id,
                    title = m.Title,
                    startsAt = m.StartsAt,
                    sourceName = m.SourceName,
                }),
                topActions = briefing.TopActions.Select(a => new
                {
                    taskId = a.TaskId,
                    projectId = a.ProjectId,
                    projectName = a.ProjectName,
                    title = a.Title,
                    nextAction = a.NextAction,
                }),
                waitingOn = briefing.WaitingOn.Select(w => new
                {
                    taskId = w.TaskId,
                    projectName = w.ProjectName,
                    title = w.Title,
                    status = w.Status,
                    updatedAt = w.UpdatedAt,
                    ageHours = w.AgeHours,
                }),
                alerts = briefing.Alerts.Select(a => new
                {
                    kind = a.Kind,
                    message = a.Message,
                    projectId = a.ProjectId,
                }),
                recentChanges = briefing.RecentChanges.Select(c => new
                {
                    revision = c.Revision,
                    entityType = c.EntityType,
                    entityId = c.EntityId,
                    changeKind = c.ChangeKind,
                    sourceEvent = c.SourceEvent,
                    createdAt = c.CreatedAt,
                }),
                changeCursor = briefing.ChangeCursor,
            },
        lastOperatorRun = lastRun is null
            ? null
            : new
            {
                id = lastRun.Id,
                triggerKind = lastRun.TriggerKind,
                status = lastRun.Status,
                briefingSummary = lastRun.BriefingSummary,
                errorText = lastRun.ErrorText,
                createdAt = lastRun.CreatedAt,
                completedAt = lastRun.CompletedAt,
            },
    };

    private static PulseBriefingStrip BuildBriefing(
        PulseView pulse,
        PulseReadStore store,
        CalendarReadStore calendar,
        ChangeLogStore changes)
    {
        var meetings = calendar.GetUpcomingContext(TimeSpan.FromDays(7), limit: 12)
            .Select(m => new PulseBriefingMeetingRecord
            {
                Id = m.Id,
                Title = m.Title,
                StartsAt = m.StartsAt,
                SourceName = m.CalendarName ?? m.MailboxName ?? m.SourceName,
            })
            .ToList();

        var topActions = pulse.Concerns
            .Where(c => !string.IsNullOrWhiteSpace(c.NextAction))
            .Take(3)
            .Select(c => new PulseBriefingActionRecord
            {
                TaskId = c.TaskId,
                ProjectId = c.ProjectId,
                ProjectName = c.ProjectName,
                Title = c.Title,
                NextAction = c.NextAction,
            })
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var waiting = pulse.Concerns
            .Where(c => string.Equals(c.Status, "waiting", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Status, "blocked", StringComparison.OrdinalIgnoreCase))
            .Select(c =>
            {
                var ageHours = 0;
                if (DateTimeOffset.TryParse(c.UpdatedAt, out var updated))
                {
                    ageHours = Math.Max(0, (int)(now - updated).TotalHours);
                }

                return new PulseBriefingWaitingRecord
                {
                    TaskId = c.TaskId,
                    ProjectName = c.ProjectName,
                    Title = c.Title,
                    Status = c.Status,
                    UpdatedAt = c.UpdatedAt,
                    AgeHours = ageHours,
                };
            })
            .OrderByDescending(w => w.AgeHours)
            .Take(8)
            .ToList();

        var alerts = new List<PulseBriefingAlertRecord>();
        var projects = store.ListOrbitProjects();
        foreach (var p in projects.Where(p => p.DossierEmpty).Take(8))
        {
            alerts.Add(new PulseBriefingAlertRecord
            {
                Kind = "empty_dossier",
                Message = $"{p.Name}: dossier is empty",
                ProjectId = p.Id,
            });
        }

        foreach (var p in projects.Where(p => p.MissingNextAction).Take(8))
        {
            alerts.Add(new PulseBriefingAlertRecord
            {
                Kind = "missing_next_action",
                Message = $"{p.Name}: open concern missing next action",
                ProjectId = p.Id,
            });
        }

        foreach (var c in pulse.Concerns.Where(c => string.IsNullOrWhiteSpace(c.BodyExcerpt)).Take(5))
        {
            alerts.Add(new PulseBriefingAlertRecord
            {
                Kind = "missing_brief",
                Message = $"{c.ProjectName}: “{c.Title}” needs a living brief",
                ProjectId = c.ProjectId,
            });
        }

        if (pulse.UnmatchedMail.Count > 0)
        {
            alerts.Add(new PulseBriefingAlertRecord
            {
                Kind = "unmatched_mail",
                Message = $"{pulse.UnmatchedMail.Count} unmatched mail item(s) need a project",
            });
        }

        // Cheap near-dupe alert: projects whose normalized names collide as substrings.
        var active = store.ListActiveProjects();
        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = ProjectIdentityMatcher.Normalize(active[i].Name);
                var b = ProjectIdentityMatcher.Normalize(active[j].Name);
                if (a.Length < 4 || b.Length < 4)
                {
                    continue;
                }

                if (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
                {
                    alerts.Add(new PulseBriefingAlertRecord
                    {
                        Kind = "ambiguous_project",
                        Message = $"Possible duplicate projects: “{active[i].Name}” and “{active[j].Name}”",
                        ProjectId = active[i].Id,
                    });
                }
            }
        }

        var cursor = changes.CurrentCursor();
        var since = Math.Max(0, cursor - 25);
        var (events, _) = changes.ListSince(since, limit: 20);
        var recent = events
            .OrderByDescending(e => e.Revision)
            .Take(12)
            .Select(e => new PulseBriefingChangeRecord
            {
                Revision = e.Revision,
                EntityType = e.EntityType,
                EntityId = e.EntityId,
                ChangeKind = e.ChangeKind,
                SourceEvent = e.SourceEvent,
                CreatedAt = e.CreatedAt,
            })
            .ToList();

        return new PulseBriefingStrip
        {
            UpcomingMeetings = meetings,
            TopActions = topActions,
            WaitingOn = waiting,
            Alerts = alerts.Take(20).ToList(),
            RecentChanges = recent,
            ChangeCursor = cursor,
        };
    }

    private static object MapOrbitProject(OrbitProjectRecord project) => new
    {
        id = project.Id,
        name = project.Name,
        summary = project.Summary,
        status = project.Status,
        inOrbit = project.InOrbit,
        openConcernCount = project.OpenConcernCount,
        topNextAction = project.TopNextAction,
        dossierEmpty = project.DossierEmpty,
        missingNextAction = project.MissingNextAction,
    };

    private static object MapIgnitionProject(OrbitIgnitionProjectResult project) => new
    {
        id = project.Id,
        name = project.Name,
        created = project.Created,
        homeFolderPath = project.HomeFolderPath,
        error = project.Error,
    };
}
