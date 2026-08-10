using System.Text.Json;
using Orbit.Core.Host.Events;
using Orbit.Infrastructure.Changes;

namespace Orbit.Core.Host.Events;

/// <summary>Writes EventHub graph events into orbit_change_log for Hermes monitor cursors.</summary>
public sealed class ChangeLogRecorderService : BackgroundService
{
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "note.created",
        "email.ingested",
        "task.updated",
        "suggestion.created",
        "calendar.synced",
        "operator.briefing",
        "pulse.refresh",
        "contact.observed",
    };

    private readonly EventHub _hub;
    private readonly ChangeLogStore _log;
    private readonly ILogger<ChangeLogRecorderService> _logger;

    public ChangeLogRecorderService(EventHub hub, ChangeLogStore log, ILogger<ChangeLogRecorderService> logger)
    {
        _hub = hub;
        _log = log;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var orbitEvent in _hub.SubscribeAsync(stoppingToken))
            {
                if (!Types.Contains(orbitEvent.Type))
                {
                    continue;
                }

                try
                {
                    Record(orbitEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Change log append failed for {Type}.", orbitEvent.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    private void Record(OrbitEvent orbitEvent)
    {
        var (entityType, entityId, tombstone) = ExtractEntity(orbitEvent);
        _log.Append(
            entityType,
            entityId,
            changeKind: "updated",
            sourceEvent: orbitEvent.Type,
            tombstone: tombstone,
            changedFieldsJson: SerializePayload(orbitEvent.Payload));
    }

    private static (string EntityType, string EntityId, bool Tombstone) ExtractEntity(OrbitEvent orbitEvent)
    {
        if (orbitEvent.Payload is null)
        {
            return ("event", orbitEvent.Type, false);
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(orbitEvent.Payload));
            var root = doc.RootElement;
            if (TryGetString(root, "taskId", out var taskId))
            {
                return ("task", taskId, false);
            }

            if (TryGetString(root, "emailId", out var emailId))
            {
                return ("email", emailId, false);
            }

            if (TryGetString(root, "noteId", out var noteId))
            {
                return ("note", noteId, false);
            }

            if (TryGetString(root, "projectId", out var projectId))
            {
                return ("project", projectId, false);
            }

            if (TryGetString(root, "id", out var id))
            {
                return (orbitEvent.Type.Split('.')[0], id, false);
            }
        }
        catch
        {
            // fall through
        }

        return ("event", orbitEvent.Type, false);
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = el.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? SerializePayload(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch
        {
            return null;
        }
    }
}
