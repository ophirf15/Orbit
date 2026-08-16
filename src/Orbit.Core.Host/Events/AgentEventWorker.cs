using System.Collections.Concurrent;
using System.Text.Json;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Core.Host.Events;

/// <summary>
/// Subscribes to EventHub, debounces meaningful events, and runs suggestion heuristics:
/// limbo note triage, task-relationship detection, inbound email matching,
/// contact reporting / org-chart proposals, and dependency-readiness checks.
/// </summary>
public sealed class AgentEventWorker : BackgroundService
{
    private static readonly HashSet<string> InterestingTypes = new(StringComparer.Ordinal)
    {
        "note.created",
        "email.ingested",
        "contact.observed",
        "task.updated",
        "task.dependency.linked",
    };

    private readonly EventHub _hub;
    private readonly SuggestionEngine _engine;
    private readonly TaskRelationshipEngine _relationships;
    private readonly ContactRelationEngine _contactRelations;
    private readonly SuggestionStore _suggestions;
    private readonly ILogger<AgentEventWorker> _logger;
    private readonly ConcurrentDictionary<string, byte> _pendingNoteIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingEmailIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingTaskIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _pendingPersonIds = new(StringComparer.Ordinal);
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;
    private int _readinessDirty;

    public AgentEventWorker(
        EventHub hub,
        SuggestionEngine engine,
        TaskRelationshipEngine relationships,
        ContactRelationEngine contactRelations,
        SuggestionStore suggestions,
        ILogger<AgentEventWorker> logger)
    {
        _hub = hub;
        _engine = engine;
        _relationships = relationships;
        _contactRelations = contactRelations;
        _suggestions = suggestions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var orbitEvent in _hub.SubscribeAsync(stoppingToken))
            {
                if (!InterestingTypes.Contains(orbitEvent.Type))
                {
                    continue;
                }

                Enqueue(orbitEvent);
                ScheduleFlush(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    private void Enqueue(OrbitEvent orbitEvent)
    {
        switch (orbitEvent.Type)
        {
            case "note.created":
                TrackId(orbitEvent.Payload, "noteId", _pendingNoteIds);
                break;

            case "email.ingested":
                TrackId(orbitEvent.Payload, "emailId", _pendingEmailIds);
                break;

            case "contact.observed":
                TrackIds(orbitEvent.Payload, "personIds", _pendingPersonIds);
                break;

            case "task.updated":
                TrackId(orbitEvent.Payload, "taskId", _pendingTaskIds);
                Interlocked.Exchange(ref _readinessDirty, 1);
                break;

            case "task.dependency.linked":
                Interlocked.Exchange(ref _readinessDirty, 1);
                break;
        }
    }

    private static void TrackId(object? payload, string property, ConcurrentDictionary<string, byte> sink)
    {
        var id = ExtractProperty(payload, property);
        if (!string.IsNullOrWhiteSpace(id))
        {
            sink[id] = 0;
        }
    }

    private static void TrackIds(object? payload, string property, ConcurrentDictionary<string, byte> sink)
    {
        if (payload is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            if (!doc.RootElement.TryGetProperty(property, out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && item.GetString() is { Length: > 0 } id)
                {
                    sink[id] = 0;
                }
            }
        }
        catch (JsonException)
        {
            // ignore malformed payloads
        }
    }

    private void ScheduleFlush(CancellationToken stoppingToken)
    {
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var token = _debounceCts.Token;
            _ = FlushAfterDebounceAsync(token);
        }
    }

    private async Task FlushAfterDebounceAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(750), token);
        }
        catch (OperationCanceledException)
        {
            // newer event reset the debounce window
            return;
        }

        try
        {
            _suggestions.ExpireOlderThan(SuggestionHygiene.DefaultExpireAge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Suggestion expire pass failed");
        }

        foreach (var noteId in Drain(_pendingNoteIds))
        {
            Run(
                () => _engine.ProcessNoteCreated(noteId),
                "SuggestionEngine failed for note {Id}",
                noteId);
        }

        foreach (var emailId in Drain(_pendingEmailIds))
        {
            Run(
                () => _relationships.SuggestMergesFromEmail(emailId),
                "Email relationship match failed for email {Id}",
                emailId);
        }

        var personIds = Drain(_pendingPersonIds);
        if (personIds.Count > 0)
        {
            Run(
                () => _contactRelations.SuggestReportingForPeople(personIds),
                "Contact reporting suggestions failed for {Id} people",
                personIds.Count.ToString());
        }

        foreach (var taskId in Drain(_pendingTaskIds))
        {
            Run(
                () => _relationships.SuggestLinksForTask(taskId),
                "Task link detection failed for task {Id}",
                taskId);
        }

        if (Interlocked.Exchange(ref _readinessDirty, 0) == 1)
        {
            Run(
                () => _relationships.SuggestReadyDependencies(),
                "Dependency readiness check failed{Id}",
                string.Empty);
        }
    }

    private static List<string> Drain(ConcurrentDictionary<string, byte> pending)
    {
        var ids = pending.Keys.ToList();
        foreach (var id in ids)
        {
            pending.TryRemove(id, out _);
        }

        return ids;
    }

    private void Run(
        Func<IReadOnlyList<AgentSuggestionRecord>> action,
        string failureTemplate,
        string contextId)
    {
        try
        {
            foreach (var suggestion in action())
            {
                PublishCreated(suggestion);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, failureTemplate, contextId);
        }
    }

    private void PublishCreated(AgentSuggestionRecord suggestion) =>
        _hub.Publish(new OrbitEvent
        {
            Type = "suggestion.created",
            Payload = new
            {
                suggestionId = suggestion.Id,
                suggestionType = suggestion.SuggestionType,
                noteId = suggestion.NoteId,
                taskId = suggestion.TaskId,
                projectId = suggestion.ProjectId,
                summary = suggestion.Summary,
            },
        });

    private static string? ExtractProperty(object? payload, string property)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            if (doc.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
