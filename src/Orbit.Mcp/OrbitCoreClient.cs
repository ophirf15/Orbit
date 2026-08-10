using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Orbit.Mcp;

/// <summary>Thin HTTP wrapper that POSTs JSON to Orbit Core Host agent tool routes.</summary>
public sealed class OrbitCoreClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public OrbitCoreClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> CallToolAsync(string toolName, object? body, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var path = OrbitToolCatalog.Route(toolName);
        var json = JsonSerializer.Serialize(body ?? new Dictionary<string, object?>(), JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(path, content, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Orbit Core tool '{toolName}' failed: {(int)response.StatusCode} {response.ReasonPhrase}. Body: {Truncate(text, 2000)}");
        }

        return string.IsNullOrWhiteSpace(text) ? "{}" : text;
    }

    public static void ConfigureHttpClient(HttpClient client, OrbitCoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? OrbitCoreOptions.DefaultCoreUrl
            : options.BaseUrl.TrimEnd('/') + "/";

        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.Timeout = TimeSpan.FromMinutes(2);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
