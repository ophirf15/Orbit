using Orbit.Core.Host;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Hermes;

namespace Orbit.Core.Host.Api;

public sealed class EmailIngestRequest
{
    /// <summary>Absolute path to a .msg file (App may copy under generated root first).</summary>
    public string? Path { get; set; }

    public List<string>? ProjectIds { get; set; }
}

public sealed class EmailProjectLinkRequest
{
    public string? ProjectId { get; set; }
}

public sealed class LinkEmailThreadRequest
{
    public string? ConversationId { get; set; }

    public string? AnchorEmailId { get; set; }

    public string? Actor { get; set; }
}

public static class EmailEndpoints
{
    public static IEndpointRouteBuilder MapEmailEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/emails/ingest", async (
            HttpContext http,
            EmailIngestionService ingest,
            EventHub hub,
            OperatorWakeService wake,
            EmailArtifactStore store,
            Orbit.Infrastructure.Operator.OperatorRunStore runs,
            Orbit.Infrastructure.Data.SqliteConnectionFactory factory) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                EmailArtifactRecord record;
                IReadOnlyList<string>? projectIds = null;

                if (http.Request.HasFormContentType)
                {
                    var form = await http.Request.ReadFormAsync();
                    projectIds = form["projectIds"]
                        .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Where(v => v.Length > 0)
                        .ToList();

                    var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                    if (file is not null && file.Length > 0)
                    {
                        await using var stream = file.OpenReadStream();
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms);
                        record = ingest.IngestBytes(ms.ToArray(), file.FileName, projectIds);
                    }
                    else
                    {
                        var path = form["path"].FirstOrDefault();
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            return Results.Json(
                                ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide a .msg file upload or path.", requestId),
                                statusCode: 400);
                        }

                        record = ingest.IngestFromPath(path, projectIds);
                    }
                }
                else
                {
                    var body = await http.Request.ReadFromJsonAsync<EmailIngestRequest>();
                    projectIds = body?.ProjectIds;
                    if (string.IsNullOrWhiteSpace(body?.Path))
                    {
                        return Results.Json(
                            ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide path to a .msg file.", requestId),
                            statusCode: 400);
                    }

                    record = ingest.IngestFromPath(body.Path, projectIds);
                }

                // Host-side clear matches (e.g. street number → project token) before Hermes wakes.
                var linked = (projectIds ?? record.ProjectIds).Distinct(StringComparer.Ordinal).ToList();
                if (linked.Count == 0)
                {
                    foreach (var match in EmailProjectAutoLinker.MatchProjects(
                                 factory, record.Subject, record.BodyPreview))
                    {
                        if (match.Confidence < 0.85)
                        {
                            continue;
                        }

                        store.LinkToProject(record.Id, match.ProjectId);
                        linked.Add(match.ProjectId);
                    }
                }

                // Re-load so projectIds reflect auto-links; preserve ingest-only flags
                // (WasExisting is not stored in SQLite and defaults to false on Get).
                var wasExisting = record.WasExisting;
                var enrichedPersonIds = record.EnrichedPersonIds;
                var enrichmentSuggestionCount = record.EnrichmentSuggestionCount;
                var claimExtractionCount = record.ClaimExtractionCount;
                var claimSuggestionId = record.ClaimSuggestionId;
                var attachmentSnapshot = record.Attachments;
                record = (store.Get(record.Id) ?? record) with
                {
                    WasExisting = wasExisting,
                    EnrichedPersonIds = enrichedPersonIds,
                    EnrichmentSuggestionCount = enrichmentSuggestionCount,
                    ClaimExtractionCount = claimExtractionCount,
                    ClaimSuggestionId = claimSuggestionId,
                    Attachments = attachmentSnapshot.Count > 0 ? attachmentSnapshot : record.Attachments,
                };
                projectIds = linked.Count > 0 ? linked : record.ProjectIds;

                hub.Publish(new OrbitEvent
                {
                    Type = "email.ingested",
                    Payload = new { emailId = record.Id, subject = record.Subject, projectIds },
                });

                // Prefer Hermes webhook (ADR 0028); fall back to slim Host wake.
                var eventId = "email-" + record.Id + "-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var wakePayload = System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "email.ingested",
                    payload = new
                    {
                        eventId,
                        emailId = record.Id,
                        subject = record.Subject,
                        projectIds,
                        bodyPreview = record.BodyPreview,
                    },
                });

                var webhooked = await TryHermesEmailWebhookAsync(
                    http.RequestServices.GetService<HostOptions>(),
                    eventId,
                    record,
                    projectIds).ConfigureAwait(false);

                if (webhooked)
                {
                    // Open a Host-side run so the App banner can wait; Hermes must complete it via
                    // orbit_report_briefing (including briefing="[SILENT]").
                    _ = runs.Start(OperatorTriggers.EmailIngested, wakePayload);
                }
                else
                {
                    wake.RequestWake(OperatorTriggers.EmailIngested, wakePayload);
                }

                if (record.EnrichedPersonIds.Count > 0)
                {
                    hub.Publish(new OrbitEvent
                    {
                        Type = "contact.observed",
                        Payload = new
                        {
                            emailId = record.Id,
                            personIds = record.EnrichedPersonIds,
                            suggestionCount = record.EnrichmentSuggestionCount,
                        },
                    });
                }

                return Results.Json(ToDto(record, requestId), statusCode: record.WasExisting
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status201Created);
            }
            catch (FileNotFoundException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
            catch (InvalidDataException ex)
            {
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: 400);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, $"Email ingest failed: {ex.Message}", requestId),
                    statusCode: 500);
            }
        });

        app.MapGet("/v1/emails/{emailId}", (string emailId, EmailArtifactStore store, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var record = store.Get(emailId);
            if (record is null)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, "Email was not found.", requestId),
                    statusCode: 404);
            }

            return Results.Json(ToDto(record, requestId));
        });

        app.MapPost("/v1/emails/{emailId}/projects", (
            string emailId,
            EmailProjectLinkRequest body,
            EmailArtifactStore store,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (string.IsNullOrWhiteSpace(body.ProjectId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide projectId.", requestId),
                        statusCode: 400);
                }

                store.LinkToProject(emailId, body.ProjectId);
                var record = store.Get(emailId);
                if (record is null)
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Email was not found.", requestId),
                        statusCode: 404);
                }

                return Results.Json(new
                {
                    id = record.Id,
                    projectIds = record.ProjectIds,
                    requestId,
                });
            }
            catch (ArgumentException ex)
            {
                var status = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ? 404 : 400;
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapPost("/v1/emails/{emailId}/open", (string emailId, TaskEmailThreadStore threads, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var path = threads.GetEmailRawPath(emailId);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Email file was not found.", requestId),
                    statusCode: 404);
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return Results.Json(new { opened = true, emailId, path, requestId });
            }
            catch (Exception ex)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.BadRequest, $"Could not open in Outlook: {ex.Message}", requestId),
                    statusCode: 400);
            }
        });

        app.MapGet("/v1/tasks/{taskId}/email-threads", (string taskId, TaskEmailThreadStore threads, HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var items = threads.ListForTask(taskId);
            return Results.Json(new
            {
                threads = items.Select(MapThread),
                requestId,
            });
        });

        app.MapPost("/v1/tasks/{taskId}/email-threads", (
            string taskId,
            LinkEmailThreadRequest body,
            TaskEmailThreadStore threads,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            try
            {
                if (string.IsNullOrWhiteSpace(body.ConversationId))
                {
                    return Results.Json(
                        ApiErrors.Create(ApiErrorCodes.BadRequest, "Provide conversationId.", requestId),
                        statusCode: 400);
                }

                var linked = threads.Link(taskId, body.ConversationId!, body.AnchorEmailId, body.Actor ?? "user");
                return Results.Json(new { thread = MapThread(linked), requestId }, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex)
            {
                var status = ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ? 404 : 400;
                return Results.Json(ApiErrors.Create(ApiErrorCodes.BadRequest, ex.Message, requestId), statusCode: status);
            }
        });

        app.MapPost("/v1/tasks/email-threads/{threadId}/unlink", (
            string threadId,
            TaskEmailThreadStore threads,
            HttpContext http) =>
        {
            var requestId = ApiKeyMiddleware.GetRequestId(http);
            var ok = threads.Unlink(threadId);
            if (!ok)
            {
                return Results.Json(
                    ApiErrors.Create(ApiErrorCodes.NotFound, "Thread link was not found.", requestId),
                    statusCode: 404);
            }

            return Results.Json(new { unlinked = true, threadId, requestId });
        });

        return app;
    }

    private static object MapThread(TaskEmailThreadRecord t) => new
    {
        id = t.Id,
        taskId = t.TaskId,
        conversationId = t.ConversationId,
        anchorEmailId = t.AnchorEmailId,
        linkedBy = t.LinkedBy,
        createdAt = t.CreatedAt,
        subject = t.Subject,
        latestSentAt = t.LatestSentAt,
        messageCount = t.MessageCount,
    };

    private static async Task<bool> TryHermesEmailWebhookAsync(
        HostOptions? options,
        string eventId,
        EmailArtifactRecord record,
        IReadOnlyList<string>? projectIds)
    {
        if (options is null || string.IsNullOrWhiteSpace(options.HermesWebhookSecret))
        {
            return false;
        }

        Uri? baseUri = null;
        if (!string.IsNullOrWhiteSpace(options.HermesWebhookBaseUrl)
            && Uri.TryCreate(options.HermesWebhookBaseUrl.Trim(), UriKind.Absolute, out var configured))
        {
            baseUri = configured;
        }
        else
        {
            baseUri = HermesWebhookClient.TryDeriveWebhookBase(options.HermesBaseUrl);
        }

        if (baseUri is null)
        {
            return false;
        }

        try
        {
            using var client = new HermesWebhookClient(baseUri);
            var result = await client.PostRouteAsync(
                "orbit-email-ingested",
                new
                {
                    eventId,
                    event_type = "email.ingested",
                    eventType = "email.ingested",
                    type = "email.ingested",
                    emailId = record.Id,
                    emailIds = new[] { record.Id },
                    conversationId = record.ConversationId,
                    subject = record.Subject,
                    projectIds = projectIds ?? record.ProjectIds,
                },
                options.HermesWebhookSecret!).ConfigureAwait(false);
            return result.Ok;
        }
        catch
        {
            return false;
        }
    }

    private static object ToDto(EmailArtifactRecord record, string requestId) =>
        new
        {
            id = record.Id,
            subject = record.Subject,
            sentAt = record.SentAt,
            receivedAt = record.ReceivedAt,
            internetMessageId = record.InternetMessageId,
            conversationId = record.ConversationId,
            bodyPreview = record.BodyPreview,
            rawPath = record.RawPath,
            bodyTextPath = record.BodyTextPath,
            bodyHtmlPath = record.BodyHtmlPath,
            contentHash = record.ContentHash,
            wasExisting = record.WasExisting,
            participants = record.Participants.Select(p => new
            {
                id = p.Id,
                role = p.Role,
                address = p.Address,
                displayName = p.DisplayName,
            }),
            projectIds = record.ProjectIds,
            attachments = record.Attachments.Select(a => new
            {
                fileName = a.FileName,
                path = a.Path,
                sizeBytes = a.SizeBytes,
            }),
            requestId,
        };
}
