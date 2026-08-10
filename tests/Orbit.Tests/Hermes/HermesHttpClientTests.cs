using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Orbit.Agent.Contracts.Hermes;
using Orbit.Infrastructure.Hermes;

namespace Orbit.Tests.Hermes;

public sealed class HermesHttpClientTests
{
    [Fact]
    public async Task HealthAsync_ParsesOkBody()
    {
        var handler = new ScriptedHandler(req =>
        {
            Assert.Equal(HttpMethod.Get, req.Method);
            Assert.EndsWith("/health", req.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            return JsonResponse(HttpStatusCode.OK, """{"status":"ok"}""");
        });

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"), "test-key");
        var result = await client.HealthAsync();
        Assert.True(result.Ok);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_404_IsDegradedNotFailure()
    {
        var handler = new ScriptedHandler(_ =>
            JsonResponse(HttpStatusCode.NotFound, """{"error":"missing"}"""));

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"));
        var caps = await client.GetCapabilitiesAsync();
        Assert.True(caps.NotFound);
        Assert.False(caps.Available);
    }

    [Fact]
    public async Task EnsureSessionAsync_FallsBackWhenSessionsMissing()
    {
        var handler = new ScriptedHandler(_ =>
            JsonResponse(HttpStatusCode.NotFound, "no sessions"));

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"));
        var session = await client.EnsureSessionAsync();
        Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
        Assert.False(session.PersistedRemotely);
    }

    [Fact]
    public async Task EnsureSessionAsync_UsesExistingId()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("should not call network"));
        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"));
        var session = await client.EnsureSessionAsync("already-there", "key-1");
        Assert.Equal("already-there", session.SessionId);
        Assert.Equal("key-1", session.SessionKey);
        Assert.True(session.PersistedRemotely);
    }

    [Fact]
    public async Task StreamChatAsync_ParsesSseDeltas_AndSendsSessionHeaders()
    {
        HttpRequestMessage? seen = null;
        var sse =
            """
            data: {"choices":[{"delta":{"content":"Hel"}}]}

            data: {"choices":[{"delta":{"content":"lo"}}]}

            data: [DONE]

            """;

        var handler = new ScriptedHandler(req =>
        {
            seen = req;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
            };
            return response;
        });

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"), "secret");
        var parts = new List<string>();
        await foreach (var delta in client.StreamChatAsync(new HermesChatRequest
        {
            SessionId = "sess-1",
            SessionKey = "key-1",
            Messages = [new HermesChatMessage { Role = "user", Content = "hi" }],
        }))
        {
            if (delta.Kind == HermesChatDeltaKind.Content && delta.Text is not null)
            {
                parts.Add(delta.Text);
            }
        }

        Assert.Equal("Hello", string.Concat(parts));
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.EndsWith("/v1/chat/completions", seen.RequestUri!.AbsolutePath, StringComparison.OrdinalIgnoreCase);
        Assert.True(seen.Headers.TryGetValues("X-Hermes-Session-Id", out var ids));
        Assert.Equal("sess-1", Assert.Single(ids));
        Assert.True(seen.Headers.TryGetValues("X-Hermes-Session-Key", out var keys));
        Assert.Equal("key-1", Assert.Single(keys));
        Assert.Equal("secret", seen.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ExtractDeltaContent_ReadsChoiceDelta()
    {
        var text = HermesHttpClient.ExtractDeltaContent(
            """{"choices":[{"delta":{"content":"token"}}]}""");
        Assert.Equal("token", text);
    }

    [Fact]
    public async Task TestConnectionAsync_SucceedsWhenHealthOk()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(HttpStatusCode.OK, """{"status":"ok"}""");
            }

            return JsonResponse(HttpStatusCode.OK, """{"models":["hermes"]}""");
        });

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"));
        var result = await client.TestConnectionAsync();
        Assert.True(result.Success);
        Assert.Contains("OK", result.HealthSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConnectionAsync_FailsOnCapabilitiesUnauthorized()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(HttpStatusCode.OK, """{"status":"ok"}""");
            }

            return JsonResponse(
                HttpStatusCode.Unauthorized,
                """{"error":{"message":"Invalid API key","code":"invalid_api_key"}}""");
        });

        using var client = new HermesHttpClient(handler, new Uri("http://192.168.1.19:8642"), "wrong");
        var result = await client.TestConnectionAsync();
        Assert.False(result.Success);
        Assert.Contains("API key", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("401", result.CapabilitiesSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionAsync_DegradesWhenCapabilitiesMissing()
    {
        var handler = new ScriptedHandler(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(HttpStatusCode.OK, """{"status":"ok"}""");
            }

            return JsonResponse(HttpStatusCode.NotFound, """{"error":"missing"}""");
        });

        using var client = new HermesHttpClient(handler, new Uri("http://127.0.0.1:8642"));
        var result = await client.TestConnectionAsync();
        Assert.True(result.Success);
        Assert.Contains("404", result.CapabilitiesSummary, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, string json)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
