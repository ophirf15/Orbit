using System.Net.Http.Headers;
using System.Text;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit_App.Services;

/// <summary>
/// Long-lived SSE client for Core Host <c>/v1/events</c>.
/// Invokes <see cref="OrbitEvent"/> when an orbit event type arrives.
/// </summary>
public sealed class OrbitEventListener : IAsyncDisposable
{
    private readonly OrbitSettings _settings;
    private readonly JsonOrbitSettingsStore _store;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public event Action<string>? OrbitEvent;

    public OrbitEventListener(OrbitSettings settings, JsonOrbitSettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false })
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already stopped
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // swallow shutdown races
            }
        }

        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ListenOnceAsync(CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_settings.CoreHostBaseUrl)
            ? OrbitSettingsDefaults.CoreHostBaseUrl
            : _settings.CoreHostBaseUrl.TrimEnd('/');

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.BaseAddress = new Uri(baseUrl + "/");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var key = _store.ReadCoreHostApiKey(_settings);
        if (!string.IsNullOrWhiteSpace(key))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/events");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var json = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            var type = TryReadType(json);
            if (!string.IsNullOrWhiteSpace(type))
            {
                OrbitEvent?.Invoke(type);
            }
        }
    }

    private static string? TryReadType(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return type.GetString();
            }
        }
        catch (Exception)
        {
            // ignore malformed frames
        }

        return null;
    }
}
