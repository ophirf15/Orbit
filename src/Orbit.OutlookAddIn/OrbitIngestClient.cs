using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Orbit.OutlookAddIn;

internal sealed class OrbitIngestClient : IDisposable
{
    private readonly HttpClient _http;

    public OrbitIngestClient(OrbitConfig.LoadedConfig config)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.CoreBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    public string? LastError { get; private set; }

    public async Task<bool> TryHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("v1/health").ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<IReadOnlyList<ProjectItem>> ListProjectsAsync()
    {
        try
        {
            using var response = await _http.GetAsync("v1/projects").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = await ReadErrorAsync(response).ConfigureAwait(false)
                            ?? $"HTTP {(int)response.StatusCode}";
                return Array.Empty<ProjectItem>();
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            var root = JObject.Parse(json);
            var list = new List<ProjectItem>();
            var arr = root["projects"] as JArray ?? root["items"] as JArray;
            if (arr is null)
            {
                return list;
            }

            foreach (var item in arr)
            {
                var id = item.Value<string>("id");
                var name = item.Value<string>("name") ?? id;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    list.Add(new ProjectItem(id!, name ?? id!));
                }
            }

            LastError = null;
            return list;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return Array.Empty<ProjectItem>();
        }
    }

    public async Task<IngestResult?> IngestMsgFileAsync(
        string msgPath,
        IReadOnlyList<string>? projectIds = null)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var bytes = File.ReadAllBytes(msgPath);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.ms-outlook");
            form.Add(fileContent, "file", Path.GetFileName(msgPath));
            if (projectIds is { Count: > 0 })
            {
                form.Add(new StringContent(string.Join(",", projectIds)), "projectIds");
            }

            using var response = await _http.PostAsync("v1/emails/ingest", form).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = TryParseMessage(body) ?? $"HTTP {(int)response.StatusCode}";
                return null;
            }

            LastError = null;
            var root = JObject.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            // Host returns flat or nested email object depending on version — accept both.
            var email = root["email"] as JObject ?? root;
            return new IngestResult(
                email.Value<string>("id") ?? root.Value<string>("id") ?? string.Empty,
                email.Value<string>("subject") ?? root.Value<string>("subject"),
                email.Value<bool?>("wasExisting") ?? root.Value<bool?>("wasExisting") ?? false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return TryParseMessage(body);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryParseMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var root = JObject.Parse(body);
            return root.Value<string>("message")
                   ?? root["error"]?.Value<string>("message")
                   ?? (body.Length > 240 ? body.Substring(0, 240) : body);
        }
        catch
        {
            return body.Length > 240 ? body.Substring(0, 240) : body;
        }
    }

    public void Dispose() => _http.Dispose();

    internal readonly struct ProjectItem
    {
        public ProjectItem(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    internal readonly struct IngestResult
    {
        public IngestResult(string id, string? subject, bool wasExisting)
        {
            Id = id;
            Subject = subject;
            WasExisting = wasExisting;
        }

        public string Id { get; }

        public string? Subject { get; }

        public bool WasExisting { get; }
    }
}
