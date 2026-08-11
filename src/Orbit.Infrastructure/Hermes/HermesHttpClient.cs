using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Agent.Contracts.Hermes;

namespace Orbit.Infrastructure.Hermes;

public sealed class HermesHttpClient : IHermesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HermesHttpClient(Uri baseAddress, string? apiKey = null)
        : this(new HttpClientHandler(), baseAddress, apiKey, disposeHandler: true)
    {
    }

    /// <summary>Test-friendly constructor that uses the provided handler (not disposed).</summary>
    public HermesHttpClient(HttpMessageHandler handler, Uri baseAddress, string? apiKey = null)
        : this(handler, baseAddress, apiKey, disposeHandler: false)
    {
    }

    private HermesHttpClient(HttpMessageHandler handler, Uri baseAddress, string? apiKey, bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(baseAddress);

        _ownsHttp = true;
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = NormalizeBase(baseAddress),
            Timeout = TimeSpan.FromMinutes(5),
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    public Uri BaseAddress => _http.BaseAddress!;

    public async Task<HermesHealthResult> HealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var ok = response.IsSuccessStatusCode &&
                 (body.Contains("ok", StringComparison.OrdinalIgnoreCase) ||
                  body.Contains("healthy", StringComparison.OrdinalIgnoreCase) ||
                  string.IsNullOrWhiteSpace(body));

        return new HermesHealthResult
        {
            Ok = ok || response.IsSuccessStatusCode,
            StatusCode = (int)response.StatusCode,
            RawBody = Truncate(body, 2000),
        };
    }

    public async Task<HermesCapabilitiesResult> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("v1/capabilities", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode == 404)
        {
            return new HermesCapabilitiesResult
            {
                Available = false,
                NotFound = true,
                StatusCode = 404,
                RawBody = Truncate(body, 2000),
            };
        }

        return new HermesCapabilitiesResult
        {
            Available = response.IsSuccessStatusCode,
            NotFound = false,
            StatusCode = (int)response.StatusCode,
            RawBody = Truncate(body, 4000),
        };
    }

    public async Task<HermesSession> EnsureSessionAsync(
        string? existingSessionId = null,
        string? existingSessionKey = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(existingSessionId))
        {
            return new HermesSession
            {
                SessionId = existingSessionId.Trim(),
                SessionKey = string.IsNullOrWhiteSpace(existingSessionKey) ? null : existingSessionKey.Trim(),
                PersistedRemotely = true,
            };
        }

        try
        {
            using var response = await _http.PostAsJsonAsync(
                "api/sessions",
                new { },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var root = doc.RootElement;
                var id = TryReadString(root, "id")
                         ?? TryReadString(root, "session_id")
                         ?? TryReadString(root, "sessionId");
                var key = TryReadString(root, "key")
                          ?? TryReadString(root, "session_key")
                          ?? TryReadString(root, "sessionKey");

                if (!string.IsNullOrWhiteSpace(id))
                {
                    return new HermesSession
                    {
                        SessionId = id,
                        SessionKey = key,
                        PersistedRemotely = true,
                    };
                }
            }
            else if ((int)response.StatusCode != 404)
            {
                // Non-404 failure: still fall back to local session so chat can proceed.
            }
        }
        catch (HttpRequestException)
        {
            // Fall through to local session.
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through to local session.
        }

        return new HermesSession
        {
            SessionId = Guid.NewGuid().ToString("D"),
            SessionKey = null,
            PersistedRemotely = false,
        };
    }

    public async IAsyncEnumerable<HermesChatDelta> StreamChatAsync(
        HermesChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Messages.Count == 0)
        {
            yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Error, Text = "At least one message is required." };
            yield break;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        if (!string.IsNullOrWhiteSpace(request.SessionId))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Hermes-Session-Id", request.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(request.SessionKey))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-Hermes-Session-Key", request.SessionKey);
        }

        var payload = new ChatCompletionsBody
        {
            Model = string.IsNullOrWhiteSpace(request.Model) ? "hermes" : request.Model,
            Stream = request.Stream,
            Messages = request.Messages
                .Select(m => new ChatMessageBody { Role = m.Role, Content = m.Content })
                .ToList(),
        };

        httpRequest.Content = JsonContent.Create(payload, options: JsonOptions);

        HttpResponseMessage? response = null;
        Stream? responseStream = null;
        StreamReader? reader = null;

        try
        {
            response = await _http.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                yield return new HermesChatDelta
                {
                    Kind = HermesChatDeltaKind.Error,
                    Text = $"Hermes chat failed ({(int)response.StatusCode}): {Truncate(errBody, 500)}",
                };
                yield break;
            }

            responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            reader = new StreamReader(responseStream, Encoding.UTF8);

            if (!request.Stream)
            {
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var text = ExtractNonStreamContent(json);
                if (!string.IsNullOrEmpty(text))
                {
                    yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Content, Text = text };
                }

                yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Done };
                yield break;
            }

            string? currentEvent = null;
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    currentEvent = null;
                    continue;
                }

                if (line[0] == ':')
                {
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEvent = line["event:".Length..].Trim();
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data.Length == 0)
                {
                    continue;
                }

                if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Done };
                    yield break;
                }

                if (IsToolProgressEvent(currentEvent))
                {
                    var progress = ExtractToolProgress(data);
                    if (progress is not null)
                    {
                        yield return progress;
                    }

                    continue;
                }

                // Some Hermes builds still inject progress markers into content — surface, don't persist as answer.
                var piece = ExtractDeltaContent(data);
                if (!string.IsNullOrEmpty(piece))
                {
                    if (LooksLikeInlineToolMarker(piece, out var markerTool))
                    {
                        yield return new HermesChatDelta
                        {
                            Kind = HermesChatDeltaKind.Progress,
                            Text = FormatToolProgressLine(markerTool, "running"),
                            ToolName = markerTool,
                            Status = "running",
                        };
                    }
                    else
                    {
                        yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Content, Text = piece };
                    }
                }

                var reasoning = ExtractReasoningDelta(data);
                if (!string.IsNullOrEmpty(reasoning))
                {
                    yield return new HermesChatDelta
                    {
                        Kind = HermesChatDeltaKind.Progress,
                        Text = Truncate(reasoning, 160),
                        Status = "thinking",
                    };
                }
            }

            yield return new HermesChatDelta { Kind = HermesChatDeltaKind.Done };
        }
        finally
        {
            reader?.Dispose();
            if (responseStream is not null)
            {
                await responseStream.DisposeAsync().ConfigureAwait(false);
            }

            response?.Dispose();
        }
    }

    public async Task<HermesConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var warning = Orbit.Core.Settings.HermesUrlValidation.GetRemoteSecurityWarning(BaseAddress.ToString());
        try
        {
            var health = await HealthAsync(cancellationToken).ConfigureAwait(false);
            if (!health.Ok)
            {
                return new HermesConnectionTestResult
                {
                    Success = false,
                    HealthSummary = $"HTTP {health.StatusCode}: {health.RawBody}",
                    Error = "Hermes health check failed.",
                    SecurityWarning = warning,
                };
            }

            var caps = await GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            string capsSummary;
            if (caps.NotFound)
            {
                capsSummary = "GET /v1/capabilities not available (404) — degraded OK.";
            }
            else if (caps.Available)
            {
                capsSummary = Truncate(caps.RawBody, 800) ?? "capabilities OK";
            }
            else
            {
                capsSummary = $"HTTP {caps.StatusCode}: {caps.RawBody}";
                var authFailed = caps.StatusCode is 401 or 403;
                return new HermesConnectionTestResult
                {
                    Success = false,
                    HealthSummary = $"OK (HTTP {health.StatusCode})",
                    CapabilitiesSummary = capsSummary,
                    Error = authFailed
                        ? "Hermes rejected the API key (capabilities 401/403). Paste the current API_SERVER_KEY from Hermes, then Connect & save. Restart Hermes after changing its key."
                        : "Hermes capabilities probe failed.",
                    SecurityWarning = warning,
                };
            }

            return new HermesConnectionTestResult
            {
                Success = true,
                HealthSummary = $"OK (HTTP {health.StatusCode})",
                CapabilitiesSummary = capsSummary,
                SecurityWarning = warning,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new HermesConnectionTestResult
            {
                Success = false,
                Error = ex.Message,
                SecurityWarning = warning,
            };
        }
    }

    public async Task<HermesRunResult?> TryStartRunAsync(
        HermesRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/runs");
            if (!string.IsNullOrWhiteSpace(request.SessionId))
            {
                httpRequest.Headers.TryAddWithoutValidation("X-Hermes-Session-Id", request.SessionId);
            }

            if (!string.IsNullOrWhiteSpace(request.SessionKey))
            {
                httpRequest.Headers.TryAddWithoutValidation("X-Hermes-Session-Key", request.SessionKey);
            }

            httpRequest.Content = JsonContent.Create(
                new
                {
                    input = request.Prompt,
                    prompt = request.Prompt,
                    model = string.IsNullOrWhiteSpace(request.Model) ? "hermes" : request.Model,
                },
                options: JsonOptions);

            using var response = await _http.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode == 404)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new HermesRunResult
                {
                    RunId = string.Empty,
                    Status = "failed",
                    SummaryText = Truncate(body, 800),
                };
            }

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = doc.RootElement;
            var runId = TryReadString(root, "id")
                        ?? TryReadString(root, "run_id")
                        ?? TryReadString(root, "runId")
                        ?? Guid.NewGuid().ToString("D");
            return new HermesRunResult
            {
                RunId = runId,
                SessionId = TryReadString(root, "session_id") ?? TryReadString(root, "sessionId") ?? request.SessionId,
                Status = TryReadString(root, "status") ?? "started",
                SummaryText = TryReadString(root, "output")
                              ?? TryReadString(root, "summary")
                              ?? Truncate(body, 2000),
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async Task<HermesOperatorChatResult> CompleteOperatorChatAsync(
        HermesChatRequest request,
        CancellationToken cancellationToken = default,
        Action<HermesChatDelta>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Stream so tool/progress events reach the duty banner while Hermes works.
        var streamRequest = new HermesChatRequest
        {
            Messages = request.Messages,
            SessionId = request.SessionId,
            SessionKey = request.SessionKey,
            Model = request.Model,
            Stream = true,
        };

        var sb = new StringBuilder();
        string? error = null;
        var sawContent = false;
        await foreach (var delta in StreamChatAsync(streamRequest, cancellationToken).ConfigureAwait(false))
        {
            if (delta.Kind == HermesChatDeltaKind.Progress)
            {
                onProgress?.Invoke(delta);
                continue;
            }

            if (delta.Kind == HermesChatDeltaKind.Content && !string.IsNullOrEmpty(delta.Text))
            {
                if (!sawContent)
                {
                    sawContent = true;
                    onProgress?.Invoke(new HermesChatDelta
                    {
                        Kind = HermesChatDeltaKind.Progress,
                        Text = "Writing the briefing…",
                        Status = "running",
                    });
                }

                sb.Append(delta.Text);
            }
            else if (delta.Kind == HermesChatDeltaKind.Error)
            {
                error = delta.Text;
            }
        }

        if (!string.IsNullOrWhiteSpace(error) && sb.Length == 0)
        {
            return new HermesOperatorChatResult
            {
                Ok = false,
                Error = error,
                SessionId = request.SessionId,
            };
        }

        return new HermesOperatorChatResult
        {
            Ok = true,
            Text = sb.ToString().Trim(),
            SessionId = request.SessionId,
            Error = error,
        };
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static Uri NormalizeBase(Uri baseAddress)
    {
        var text = baseAddress.ToString().TrimEnd('/') + "/";
        return new Uri(text, UriKind.Absolute);
    }

    private static string? TryReadString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!root.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
    }

    public static string? ExtractDeltaContent(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var msgContent) &&
                msgContent.ValueKind == JsonValueKind.String)
            {
                return msgContent.GetString();
            }

            if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static string? ExtractReasoningDelta(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
            {
                return null;
            }

            foreach (var name in new[] { "reasoning_content", "reasoning", "thinking" })
            {
                if (delta.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    var value = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value.Trim();
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static HermesChatDelta? ExtractToolProgress(string dataJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            var tool = TryReadFlexibleString(root, "tool")
                       ?? TryReadFlexibleString(root, "name")
                       ?? TryReadFlexibleString(root, "toolName")
                       ?? TryReadFlexibleString(root, "tool_name");
            var status = TryReadFlexibleString(root, "status") ?? "running";
            var message = TryReadFlexibleString(root, "message")
                          ?? TryReadFlexibleString(root, "text")
                          ?? TryReadFlexibleString(root, "detail");

            // Nested payload wrappers
            if (tool is null && root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                tool = TryReadFlexibleString(payload, "tool")
                       ?? TryReadFlexibleString(payload, "name");
                status = TryReadFlexibleString(payload, "status") ?? status;
                message = TryReadFlexibleString(payload, "message") ?? message;
            }

            if (string.IsNullOrWhiteSpace(tool) && string.IsNullOrWhiteSpace(message))
            {
                // Plain string progress body
                if (root.ValueKind == JsonValueKind.String)
                {
                    message = root.GetString();
                }
                else
                {
                    return null;
                }
            }

            var line = !string.IsNullOrWhiteSpace(message)
                ? message!.Trim()
                : FormatToolProgressLine(tool!, status);

            return new HermesChatDelta
            {
                Kind = HermesChatDeltaKind.Progress,
                Text = line,
                ToolName = tool,
                Status = status,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string FormatToolProgressLine(string toolName, string? status)
    {
        var pretty = PrettifyToolName(toolName);
        var st = (status ?? "running").Trim().ToLowerInvariant();
        return st switch
        {
            "completed" or "done" or "ok" or "success" => $"Finished {pretty}",
            "failed" or "error" => $"Failed on {pretty}",
            _ => $"Using {pretty}…",
        };
    }

    public static string PrettifyToolName(string toolName)
    {
        var name = toolName.Trim();
        while (true)
        {
            if (name.StartsWith("mcp_orbit_", StringComparison.OrdinalIgnoreCase))
            {
                name = name["mcp_orbit_".Length..];
                continue;
            }

            if (name.StartsWith("orbit_", StringComparison.OrdinalIgnoreCase))
            {
                name = name["orbit_".Length..];
                continue;
            }

            break;
        }

        name = name.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? "a tool" : name;
    }

    private static bool IsToolProgressEvent(string? eventName) =>
        !string.IsNullOrWhiteSpace(eventName)
        && (eventName.Equals("hermes.tool.progress", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("tool.progress", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("tool.started", StringComparison.OrdinalIgnoreCase)
            || eventName.Equals("tool.completed", StringComparison.OrdinalIgnoreCase)
            || eventName.Contains("tool", StringComparison.OrdinalIgnoreCase)
               && eventName.Contains("progress", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeInlineToolMarker(string piece, out string toolName)
    {
        toolName = string.Empty;
        var trimmed = piece.Trim();
        // Legacy markers like "⏰ web_search" or "`🔍 orbit_search`"
        if (trimmed.Length < 3 || trimmed.Length > 80)
        {
            return false;
        }

        var cleaned = trimmed.Trim('`', '*', ' ', '\t');
        foreach (var prefix in new[] { "⏰", "🔍", "🛠", "⚙️", "🔧" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.Ordinal))
            {
                cleaned = cleaned[prefix.Length..].Trim();
                break;
            }
        }

        if (cleaned.Contains(' ') || cleaned.Contains('\n'))
        {
            return false;
        }

        if (!cleaned.Contains('_', StringComparison.Ordinal)
            && !cleaned.Contains("orbit", StringComparison.OrdinalIgnoreCase)
            && !cleaned.Contains("search", StringComparison.OrdinalIgnoreCase)
            && !cleaned.Contains("skill", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        toolName = cleaned;
        return true;
    }

    private static string? TryReadFlexibleString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? ExtractNonStreamContent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static string? Truncate(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    private sealed class ChatCompletionsBody
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("messages")]
        public List<ChatMessageBody> Messages { get; set; } = [];
    }

    private sealed class ChatMessageBody
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";
    }
}
