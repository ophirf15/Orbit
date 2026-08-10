using System.Text;
using Newtonsoft.Json.Linq;

namespace Orbit.OutlookAddIn;

/// <summary>
/// Reads Orbit App settings + Core API key from %LocalAppData%\Orbit
/// (same sidecars Settings → Connect / Core Host write).
/// </summary>
internal static class OrbitConfig
{
    public static string AppRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit");

    public static string SettingsPath => Path.Combine(AppRoot, "settings.json");

    public static LoadedConfig Load()
    {
        var coreUrl = "http://127.0.0.1:8741";
        string? keyPath = Path.Combine(AppRoot, "core-host-api-key.txt");

        if (File.Exists(SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(SettingsPath);
                var root = JObject.Parse(json);
                var url = root.Value<string>("coreHostBaseUrl");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    coreUrl = url.Trim().TrimEnd('/');
                }

                var keyRef = root.Value<string>("coreHostApiKeyReference");
                if (!string.IsNullOrWhiteSpace(keyRef))
                {
                    keyPath = Path.IsPathRooted(keyRef)
                        ? keyRef
                        : Path.Combine(AppRoot, keyRef);
                }
            }
            catch
            {
                // keep defaults
            }
        }

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
        {
            apiKey = File.ReadAllText(keyPath).Trim();
            if (apiKey.Length == 0)
            {
                apiKey = null;
            }
        }

        return new LoadedConfig(coreUrl, apiKey);
    }

    public readonly struct LoadedConfig
    {
        public LoadedConfig(string coreBaseUrl, string? apiKey)
        {
            CoreBaseUrl = coreBaseUrl;
            ApiKey = apiKey;
        }

        public string CoreBaseUrl { get; }

        public string? ApiKey { get; }

        public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    }
}
