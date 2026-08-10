using System.Text.Json;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Pulse;

namespace Orbit.Core.Host.Api;

/// <summary>Hermes monitor fuel: changes cursor, pulse delta, blocked tasks, stable agent snapshot (ADR 0028).</summary>
public static class AgentMonitorEndpoints
{
    public static IEndpointRouteBuilder MapAgentMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(HostEndpoints.Changes, GetChanges);
        app.MapGet(HostEndpoints.PulseDelta, GetPulseDelta);
        app.MapGet(HostEndpoints.TasksBlocked, GetBlockedTasks);
        app.MapGet(HostEndpoints.AgentSnapshot, GetAgentSnapshot);
        return app;
    }

    private static IResult GetChanges(ChangeLogStore log, HttpContext http, long? cursor = null, int? limit = null)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var (events, next) = log.ListSince(cursor ?? 0, limit ?? 200);
        return Results.Json(new
        {
            cursor = cursor ?? 0,
            nextCursor = next,
            events = events.Select(e => new
            {
                revision = e.Revision,
                entityType = e.EntityType,
                entityId = e.EntityId,
                changeKind = e.ChangeKind,
                sourceEvent = e.SourceEvent,
                tombstone = e.Tombstone,
                changedFields = e.ChangedFieldsJson,
                // createdAt omitted from monitor hash surface — available for debug via revision order
            }),
            requestId,
        });
    }

    private static IResult GetPulseDelta(
        ChangeLogStore log,
        PulseReadStore pulse,
        HttpContext http,
        long? cursor = null,
        int? limit = null)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var (events, next) = log.ListSince(cursor ?? 0, limit ?? 200);
        var taskEvents = events
            .Where(e => string.Equals(e.EntityType, "task", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.EntityType, "project", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.SourceEvent, "task.updated", StringComparison.Ordinal)
                || string.Equals(e.SourceEvent, "operator.briefing", StringComparison.Ordinal))
            .ToList();

        var concerns = pulse.GetPulse().Concerns
            .OrderBy(c => c.TaskId, StringComparer.Ordinal)
            .Select(c => new
            {
                taskId = c.TaskId,
                projectId = c.ProjectId,
                projectName = c.ProjectName,
                title = c.Title,
                status = c.Status,
                nextAction = c.NextAction,
            })
            .ToList();

        return Results.Json(new
        {
            cursor = cursor ?? 0,
            nextCursor = next,
            changed = taskEvents.Select(e => new
            {
                revision = e.Revision,
                entityType = e.EntityType,
                entityId = e.EntityId,
                sourceEvent = e.SourceEvent,
                tombstone = e.Tombstone,
            }),
            concerns,
            requestId,
        });
    }

    private static IResult GetBlockedTasks(
        SqliteConnectionFactory factory,
        HttpContext http,
        string? projectId = null,
        int? limit = null)
    {
        var requestId = ApiKeyMiddleware.GetRequestId(http);
        var take = Math.Clamp(limit ?? 100, 1, 300);
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(projectId))
        {
            cmd.CommandText =
                """
                SELECT t.id, t.project_id, p.name, t.title, t.status, t.next_action, t.body
                FROM tasks t
                JOIN projects p ON p.id = t.project_id
                WHERE t.status = $blocked AND t.archived_at IS NULL
                ORDER BY t.id ASC
                LIMIT $limit;
                """;
        }
        else
        {
            cmd.CommandText =
                """
                SELECT t.id, t.project_id, p.name, t.title, t.status, t.next_action, t.body
                FROM tasks t
                JOIN projects p ON p.id = t.project_id
                WHERE t.status = $blocked AND t.archived_at IS NULL AND t.project_id = $projectId
                ORDER BY t.id ASC
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$projectId", projectId.Trim());
        }

        cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
        cmd.Parameters.AddWithValue("$limit", take);

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
                body = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }

        return Results.Json(new { tasks = rows, requestId });
    }

    private static IResult GetAgentSnapshot(
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
            cmd.CommandText =
                """
                SELECT id, name, status, COALESCE(in_orbit, 0)
                FROM projects
                WHERE archived_at IS NULL
                ORDER BY id ASC;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(new
                {
                    id = reader.GetString(0),
                    name = reader.GetString(1),
                    status = reader.GetString(2),
                    inOrbit = reader.GetInt64(3) != 0,
                });
            }
        }

        var tasks = new List<object>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id, project_id, title, status, COALESCE(next_action, ''), COALESCE(priority, 0), COALESCE(urgency, -1)
                FROM tasks
                WHERE archived_at IS NULL
                  AND status IN ($blocked, $waiting, $active, $notStarted)
                ORDER BY id ASC;
                """;
            cmd.Parameters.AddWithValue("$blocked", TaskStatuses.Blocked);
            cmd.Parameters.AddWithValue("$waiting", TaskStatuses.Waiting);
            cmd.Parameters.AddWithValue("$active", TaskStatuses.Active);
            cmd.Parameters.AddWithValue("$notStarted", TaskStatuses.NotStarted);
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
            .Select(m => new
            {
                id = m.Id,
                title = m.Title,
                // StartsAt intentionally excluded — volatile relative to "now" windows; use id+title+score for hash stability
                attentionScore = m.AttentionScore,
            })
            .ToList();

        // Stable JSON: fixed property order via anonymous types + sorted collections; no timestamps.
        return Results.Json(new
        {
            schema = "orbit.agent.snapshot.v1",
            changeCursor = cursor,
            projects,
            tasks,
            meetings,
            requestId,
        });
    }
}
