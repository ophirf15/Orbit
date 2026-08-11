using System.Collections.Concurrent;
using System.Text.Json;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Core.Agent;
using Orbit.Core.Host;
using Orbit.Core.Operator;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Hermes;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Pulse;

namespace Orbit.Core.Host.Events;

/// <summary>
/// Debounces real graph events (email/note/task/suggestion), applies standing rules, then wakes
/// Hermes for a briefing. ADR 0028: no periodic calendar.soon or duty.scan agent polling — Hermes
/// cron owns cadence. Falls back silently when Hermes is unreachable (heuristic AgentEventWorker
/// still runs).
/// </summary>
public sealed class OperatorWakeService : BackgroundService
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(45);

    public const int MaxConcurrentRuns = 1;

    public const string OperatorSessionKey = "orbit-operator";

    private static readonly HashSet<string> InterestingTypes = new(StringComparer.Ordinal)
    {
        "note.created",
        "email.ingested",
        "task.updated",
        "suggestion.created",
        // calendar.synced is data-only (ADR 0028). Hermes cron owns calendar cadence.
    };

    private readonly EventHub _hub;
    private readonly HostOptions _options;
    private readonly StandingRuleEngine _ruleEngine;
    private readonly OperatorRunStore _runs;
    private readonly EmailArtifactStore _emails;
    private readonly EmailDutyEnsureService _dutyEnsure;
    private readonly HermesHealthStatusStoreBridge _health;
    private readonly PulseReadStore _pulse;
    private readonly OperatorMemoryStore _memory;
    private readonly ILogger<OperatorWakeService> _logger;
    private readonly ConcurrentQueue<WakeItem> _queue = new();
    private readonly object _debounceGate = new();
    private CancellationTokenSource? _debounceCts;
    private int _running;
    private CancellationToken _stopping = CancellationToken.None;

    public OperatorWakeService(
        EventHub hub,
        HostOptions options,
        StandingRuleEngine ruleEngine,
        OperatorRunStore runs,
        EmailArtifactStore emails,
        EmailDutyEnsureService dutyEnsure,
        HermesHealthStatusStoreBridge health,
        PulseReadStore pulse,
        OperatorMemoryStore memory,
        ILogger<OperatorWakeService> logger)
    {
        _hub = hub;
        _options = options;
        _ruleEngine = ruleEngine;
        _runs = runs;
        _emails = emails;
        _dutyEnsure = dutyEnsure;
        _health = health;
        _pulse = pulse;
        _memory = memory;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cleared = _runs.AbandonAllRunning(
                "Cleared on Host startup (previous session interrupted).");
            if (cleared > 0)
            {
                _logger.LogWarning(
                    "Abandoned {Count} stuck operator run(s) left running from a prior Host process.",
                    cleared);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear stuck operator runs on startup.");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        // ADR 0028: no Host calendar.soon LLM loop — Hermes cron + change feed own cadence.

        try
        {
            await foreach (var orbitEvent in _hub.SubscribeAsync(stoppingToken))
            {
                if (!InterestingTypes.Contains(orbitEvent.Type))
                {
                    continue;
                }

                Enqueue(MapTrigger(orbitEvent.Type), SerializePayload(orbitEvent));
                ScheduleFlush(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    /// <summary>Explicit wake (e.g. after email ingest) so duty runs even if EventHub subscribers race.</summary>
    public void RequestWake(string triggerKind, string? payloadJson = null)
    {
        Enqueue(triggerKind, payloadJson);
        if (!_stopping.IsCancellationRequested)
        {
            ScheduleFlush(_stopping);
        }
    }

    private void Enqueue(string trigger, string? payload)
    {
        _queue.Enqueue(new WakeItem(trigger, payload, DateTimeOffset.UtcNow));
    }

    private void ScheduleFlush(CancellationToken stoppingToken)
    {
        lock (_debounceGate)
        {
            _debounceCts?.Cancel();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var cts = _debounceCts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750, cts.Token).ConfigureAwait(false);
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // debounced
                }
            }, CancellationToken.None);
        }
    }

    private async Task FlushAsync(CancellationToken stoppingToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // Short stale window — a crash mid-Hermes must not block the next mail push for 12 minutes.
            _runs.AbandonStaleRunning(
                TimeSpan.FromMinutes(3),
                reason: "Abandoned stale operator run (exceeded 3m).");

            if (_runs.CountRunning() >= MaxConcurrentRuns)
            {
                // Ingest opens an email.ingested shell before Host wake; that must not deadlock Flush.
                var blocking = _runs.ListRecent(20)
                    .Where(r => string.Equals(r.Status, OperatorRunStatuses.Running, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var onlyEmailShells = blocking.Count > 0
                    && blocking.All(r => r.TriggerKind.Contains("email", StringComparison.OrdinalIgnoreCase));
                if (!onlyEmailShells)
                {
                    RescheduleAfter(TimeSpan.FromSeconds(2), stoppingToken);
                    return;
                }
            }

            var last = _runs.LastCompletedUtc();
            if (last is not null)
            {
                var since = DateTimeOffset.UtcNow - last.Value;
                // Email shells waiting for Host must not sit behind pulse.refresh's 45s cooldown.
                var emailShellWaiting = _runs.ListRecent(10).Any(r =>
                    string.Equals(r.Status, OperatorRunStatuses.Running, StringComparison.OrdinalIgnoreCase)
                    && r.TriggerKind.Contains("email", StringComparison.OrdinalIgnoreCase));
                var cooldown = emailShellWaiting ? TimeSpan.FromSeconds(2) : DefaultCooldown;
                if (since < cooldown)
                {
                    RescheduleAfter(cooldown - since + TimeSpan.FromMilliseconds(250), stoppingToken);
                    return;
                }
            }

            if (!_queue.TryDequeue(out var item))
            {
                return;
            }

            // Drain coalesce — keep newest of same trigger batch.
            while (_queue.TryDequeue(out var more))
            {
                item = more;
            }

            ApplyRules(item);

            if (!HermesReachable())
            {
                var skipped = _runs.Start(item.Trigger, item.PayloadJson);
                EnsureEmailDutyFloor(item);
                _runs.Complete(
                    skipped.Id,
                    OperatorRunStatuses.Skipped,
                    briefingSummary: "Hermes unreachable; Host ensured task brief when possible.");
                return;
            }

            await RunOperatorAsync(item, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            if (!_queue.IsEmpty)
            {
                ScheduleFlush(stoppingToken);
            }
        }
    }

    private void RescheduleAfter(TimeSpan delay, CancellationToken stoppingToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                ScheduleFlush(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }, CancellationToken.None);
    }

    private void ApplyRules(WakeItem item)
    {
        try
        {
            var ctx = BuildMatchContext(item);
            var applied = _ruleEngine.ApplyMatching(item.Trigger, ctx);
            if (applied.Any(a => a.Applied))
            {
                _logger.LogInformation(
                    "Standing rules applied {Count} action(s) for {Trigger}.",
                    applied.Count(a => a.Applied),
                    item.Trigger);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standing rule apply failed for {Trigger}.", item.Trigger);
        }
    }

    private async Task RunOperatorAsync(WakeItem item, CancellationToken stoppingToken)
    {
        var emailId = ExtractEmailId(item.PayloadJson);
        var run = _runs.FindRunning(item.Trigger, emailId)
                  ?? _runs.Start(item.Trigger, item.PayloadJson);
        _runs.SetProgress(run.Id, "Waking Hermes…");
        try
        {
            using var client = CreateHermesClient();
            if (client is null)
            {
                EnsureEmailDutyFloor(item);
                _runs.Complete(run.Id, OperatorRunStatuses.Skipped, briefingSummary: "Hermes not configured.");
                return;
            }

            var health = await client.HealthAsync(stoppingToken).ConfigureAwait(false);
            _health.Write(_options.LocalDataRoot, health.Ok, health.StatusCode, health.RawBody);
            if (!health.Ok)
            {
                EnsureEmailDutyFloor(item);
                _runs.Complete(run.Id, OperatorRunStatuses.Skipped, briefingSummary: "Hermes health failed.");
                return;
            }

            _runs.SetProgress(run.Id, "Opening a Hermes session…");
            var priorSession = _runs.ListRecent(5)
                .Select(r => r.HermesSessionId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            var session = await client.EnsureSessionAsync(
                existingSessionId: priorSession,
                existingSessionKey: OperatorSessionKey,
                cancellationToken: stoppingToken).ConfigureAwait(false);

            var emailSnapshot = BuildEmailSnapshot(item);
            var relationMemory = EmailRelationMemory.ListRecentFactLines(_memory, limit: 12);
            var system = OperatorPromptBuilder.Build(
                item.Trigger,
                item.PayloadJson,
                emailSnapshotJson: emailSnapshot,
                emailRelationMemory: relationMemory);
            var user = string.IsNullOrWhiteSpace(emailSnapshot)
                ? "Produce the duty briefing for this trigger. Use Orbit tools as needed."
                : "The email snapshot below is authoritative — do not claim the email is missing. Link/update projects and tasks, then brief what you did.";

            _runs.SetProgress(run.Id, "Asking Hermes to organize…");
            var runResult = await client.TryStartRunAsync(
                new HermesRunRequest
                {
                    Prompt = system + "\n\n" + user,
                    SessionId = session.SessionId,
                    SessionKey = session.SessionKey ?? OperatorSessionKey,
                },
                stoppingToken).ConfigureAwait(false);

            string? briefing;
            string? hermesRunId = null;
            if (TryUseRunBriefing(runResult, out briefing, out hermesRunId))
            {
                // synchronous run returned a real briefing
            }
            else
            {
                _runs.SetProgress(run.Id, "Reading the email, matching projects…");
                DateTime lastProgressWrite = DateTime.MinValue;
                var chat = await client.CompleteOperatorChatAsync(
                    new HermesChatRequest
                    {
                        SessionId = session.SessionId,
                        SessionKey = session.SessionKey ?? OperatorSessionKey,
                        Stream = true,
                        Messages =
                        [
                            new HermesChatMessage { Role = "system", Content = system },
                            new HermesChatMessage { Role = "user", Content = user },
                        ],
                    },
                    stoppingToken,
                    onProgress: delta =>
                    {
                        var line = FormatOperatorProgress(delta);
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            return;
                        }

                        // Avoid hammering SQLite on rapid tool ticks.
                        var now = DateTime.UtcNow;
                        if (now - lastProgressWrite < TimeSpan.FromMilliseconds(400)
                            && !string.Equals(delta.Status, "completed", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        lastProgressWrite = now;
                        _runs.SetProgress(run.Id, line);
                    }).ConfigureAwait(false);

                if (!chat.Ok)
                {
                    EnsureEmailDutyFloor(item);
                    _runs.Complete(
                        run.Id,
                        OperatorRunStatuses.Failed,
                        errorText: chat.Error,
                        hermesSessionId: session.SessionId,
                        hermesRunId: hermesRunId);
                    return;
                }

                briefing = chat.Text;
            }

            EnsureEmailDutyFloor(item);
            var silent = IsSilentBriefing(briefing);
            var briefingText = silent
                ? "Nothing material to surface."
                : string.IsNullOrWhiteSpace(briefing)
                    ? "Hermes finished this email run (no briefing text)."
                    : Truncate(briefing, 4000)!;
            _runs.Complete(
                run.Id,
                OperatorRunStatuses.Completed,
                briefingSummary: briefingText,
                hermesSessionId: session.SessionId,
                hermesRunId: hermesRunId);
            if (!silent)
            {
                PersistPulseBriefing(run.Id, item.Trigger, briefingText);
            }

            _hub.Publish(new OrbitEvent
            {
                Type = "operator.briefing",
                Payload = new { runId = run.Id, trigger = item.Trigger, briefing = briefingText, silent },
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Operator wake failed.");
            EnsureEmailDutyFloor(item);
            _runs.Complete(run.Id, OperatorRunStatuses.Failed, errorText: ex.Message);
        }
    }

    private static bool IsSilentBriefing(string? briefing)
    {
        if (string.IsNullOrWhiteSpace(briefing))
        {
            return false;
        }

        var t = briefing.Trim();
        return t.Equals("[SILENT]", StringComparison.OrdinalIgnoreCase)
               || t.Equals("SILENT", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FormatOperatorProgress(HermesChatDelta delta)
    {
        if (!string.IsNullOrWhiteSpace(delta.Text))
        {
            return delta.Text.Trim();
        }

        if (!string.IsNullOrWhiteSpace(delta.ToolName))
        {
            return HermesHttpClient.FormatToolProgressLine(delta.ToolName, delta.Status);
        }

        return null;
    }

    private void PersistPulseBriefing(string runId, string trigger, string? briefing)
    {
        if (string.IsNullOrWhiteSpace(briefing) || IsSilentBriefing(briefing))
        {
            return;
        }

        try
        {
            var dayBrief = Truncate(briefing, 2500);
            var payload = JsonSerializer.Serialize(new
            {
                source = "operator.wake",
                runId,
                trigger,
                savedAt = DateTimeOffset.UtcNow.ToString("O"),
            });
            _pulse.SaveSnapshot(dayBrief, payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist operator briefing to pulse snapshot.");
        }
    }

    private void EnsureEmailDutyFloor(WakeItem item)
    {
        if (!string.Equals(item.Trigger, OperatorTriggers.EmailIngested, StringComparison.Ordinal))
        {
            return;
        }

        var emailId = ExtractEmailId(item.PayloadJson);
        if (string.IsNullOrWhiteSpace(emailId))
        {
            return;
        }

        try
        {
            var result = _dutyEnsure.Ensure(emailId);
            if (result.Ok)
            {
                _logger.LogInformation("Duty ensure: {Detail}", result.Detail);
                _hub.Publish(new OrbitEvent
                {
                    Type = "task.updated",
                    Payload = new { taskId = result.TaskId, projectId = result.ProjectId, emailId },
                });
            }
            else
            {
                _logger.LogInformation("Duty ensure skipped: {Detail}", result.Detail);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Duty ensure failed for {EmailId}.", emailId);
        }
    }

    private bool HermesReachable()
    {
        if (string.IsNullOrWhiteSpace(_options.HermesBaseUrl))
        {
            return false;
        }

        // Prefer a live probe — stale diagnostics JSON caused silent skips while Hermes was up.
        try
        {
            using var client = CreateHermesClient();
            if (client is null)
            {
                return false;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var health = client.HealthAsync(cts.Token).GetAwaiter().GetResult();
            _health.Write(_options.LocalDataRoot, health.Ok, health.StatusCode, health.RawBody);
            return health.Ok;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Live Hermes reachability probe failed.");
            var last = _health.Read(_options.LocalDataRoot);
            return last?.Ok == true;
        }
    }

    private HermesHttpClient? CreateHermesClient()
    {
        if (!HermesUrlValidation.TryValidate(_options.HermesBaseUrl, out var normalized, out _))
        {
            return null;
        }

        return new HermesHttpClient(new Uri(normalized!), _options.HermesApiKey);
    }

    private static string MapTrigger(string eventType) => eventType switch
    {
        "email.ingested" => OperatorTriggers.EmailIngested,
        "note.created" => OperatorTriggers.NoteCreated,
        "task.updated" => OperatorTriggers.TaskUpdated,
        _ => eventType,
    };

    private static string? SerializePayload(OrbitEvent orbitEvent)
    {
        try
        {
            return JsonSerializer.Serialize(new { type = orbitEvent.Type, payload = orbitEvent.Payload });
        }
        catch
        {
            return orbitEvent.Type;
        }
    }

    private static OperatorMatchContext BuildMatchContext(WakeItem item)
    {
        string? projectId = null;
        string? emailId = null;
        string? taskId = null;
        string? subject = null;
        if (!string.IsNullOrWhiteSpace(item.PayloadJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(item.PayloadJson);
                if (doc.RootElement.TryGetProperty("payload", out var payload)
                    && payload.ValueKind == JsonValueKind.Object)
                {
                    projectId = ReadString(payload, "projectId");
                    if (projectId is null
                        && payload.TryGetProperty("projectIds", out var ids)
                        && ids.ValueKind == JsonValueKind.Array
                        && ids.GetArrayLength() > 0)
                    {
                        projectId = ids[0].GetString();
                    }

                    emailId = ReadString(payload, "emailId") ?? ReadString(payload, "id");
                    taskId = ReadString(payload, "taskId");
                    subject = ReadString(payload, "subject");
                }
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        return new OperatorMatchContext
        {
            ProjectId = projectId,
            EmailId = emailId,
            TaskId = taskId,
            Subject = subject,
        };
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? el.GetString() : null;

    private string? BuildEmailSnapshot(WakeItem item)
    {
        var emailId = ExtractEmailId(item.PayloadJson);
        if (string.IsNullOrWhiteSpace(emailId))
        {
            return null;
        }

        var record = _emails.Get(emailId);
        if (record is null)
        {
            return null;
        }

        string? bodyText = record.BodyPreview;
        if (!string.IsNullOrWhiteSpace(record.BodyTextPath) && File.Exists(record.BodyTextPath))
        {
            try
            {
                bodyText = File.ReadAllText(record.BodyTextPath);
                if (bodyText.Length > 4000)
                {
                    bodyText = bodyText[..4000] + "…";
                }
            }
            catch
            {
                // keep preview
            }
        }

        return JsonSerializer.Serialize(new
        {
            emailId = record.Id,
            subject = record.Subject,
            sentAt = record.SentAt,
            bodyPreview = record.BodyPreview,
            bodyText,
            projectIds = record.ProjectIds,
            participants = record.Participants.Select(p => new
            {
                role = p.Role,
                address = p.Address,
                displayName = p.DisplayName,
            }),
        });
    }

    private static string? ExtractEmailId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object)
            {
                return ReadString(payload, "emailId") ?? ReadString(payload, "id");
            }

            return ReadString(doc.RootElement, "emailId");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryUseRunBriefing(
        HermesRunResult? runResult,
        out string? briefing,
        out string? hermesRunId)
    {
        briefing = null;
        hermesRunId = null;
        if (runResult is null || string.IsNullOrWhiteSpace(runResult.SummaryText))
        {
            return false;
        }

        hermesRunId = string.IsNullOrWhiteSpace(runResult.RunId) ? null : runResult.RunId;
        var status = (runResult.Status ?? string.Empty).Trim();
        if (status.Equals("started", StringComparison.OrdinalIgnoreCase)
            || status.Equals("queued", StringComparison.OrdinalIgnoreCase)
            || status.Equals("running", StringComparison.OrdinalIgnoreCase)
            || status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            // Async run ACK — not a briefing. Fall through to chat completions.
            return false;
        }

        var text = runResult.SummaryText.Trim();
        if (text.StartsWith('{')
            && text.Contains("\"status\"", StringComparison.OrdinalIgnoreCase)
            && text.Contains("started", StringComparison.OrdinalIgnoreCase)
            && text.Length < 400)
        {
            return false;
        }

        briefing = text;
        return true;
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed record WakeItem(string Trigger, string? PayloadJson, DateTimeOffset EnqueuedAt);
}

/// <summary>Thin bridge so Host does not take a hard dependency cycle on Diagnostics namespace naming.</summary>
public sealed class HermesHealthStatusStoreBridge
{
    private readonly Orbit.Infrastructure.Diagnostics.HermesHealthStatusStore _store = new();

    public void Write(string localDataRoot, bool ok, int statusCode, string? summary) =>
        _store.Write(localDataRoot, new Orbit.Infrastructure.Diagnostics.HermesHealthLastKnown
        {
            Ok = ok,
            StatusCode = statusCode,
            Summary = summary,
            CheckedAtUtc = DateTime.UtcNow.ToString("O"),
        });

    public Orbit.Infrastructure.Diagnostics.HermesHealthLastKnown? Read(string localDataRoot) =>
        _store.Read(localDataRoot);
}
