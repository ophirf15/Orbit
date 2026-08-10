using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Orbit.Infrastructure.Hermes;

/// <summary>Posts signed events to Hermes webhook routes (ADR 0028). Port defaults to 8644.</summary>
public sealed class HermesWebhookClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public HermesWebhookClient(Uri webhookBaseAddress, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(webhookBaseAddress);
        if (handler is null)
        {
            _ownsHttp = true;
            _http = new HttpClient { BaseAddress = Normalize(webhookBaseAddress), Timeout = TimeSpan.FromSeconds(30) };
        }
        else
        {
            _ownsHttp = true;
            _http = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = Normalize(webhookBaseAddress),
                Timeout = TimeSpan.FromSeconds(30),
            };
        }
    }

    public static Uri? TryDeriveWebhookBase(string? hermesApiBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(hermesApiBaseUrl)
            || !Uri.TryCreate(hermesApiBaseUrl.Trim(), UriKind.Absolute, out var api))
        {
            return null;
        }

        var builder = new UriBuilder(api) { Port = 8644, Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
        return builder.Uri;
    }

    public async Task<HermesWebhookPostResult> PostRouteAsync(
        string routeName,
        object payload,
        string hmacSecret,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(hmacSecret);

        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var v2Payload = Encoding.UTF8.GetBytes(timestamp + "." + json);
        var v2Hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hmacSecret), v2Payload))
            .ToLowerInvariant();
        var v1Hex = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hmacSecret), bytes))
            .ToLowerInvariant();

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/" + routeName.Trim().TrimStart('/'))
        {
            Content = content,
        };
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature-V2", v2Hex);
        request.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Webhook-Signature", v1Hex);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new HermesWebhookPostResult
            {
                Ok = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                Body = Truncate(body, 1000),
            };
        }
        catch (Exception ex)
        {
            return new HermesWebhookPostResult
            {
                Ok = false,
                StatusCode = 0,
                Body = Truncate(ex.Message, 1000),
            };
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static Uri Normalize(Uri uri)
    {
        var text = uri.ToString().TrimEnd('/') + "/";
        return new Uri(text, UriKind.Absolute);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}

public sealed class HermesWebhookPostResult
{
    public bool Ok { get; init; }

    public int StatusCode { get; init; }

    public string? Body { get; init; }
}
