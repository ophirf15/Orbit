namespace Orbit.Mcp;

public sealed class OrbitCoreOptions
{
    public const string CoreUrlEnv = "ORBIT_CORE_URL";
    public const string ApiKeyEnv = "ORBIT_API_KEY";
    public const string DefaultCoreUrl = "http://127.0.0.1:8741";

    public string BaseUrl { get; set; } = DefaultCoreUrl;

    public string? ApiKey { get; set; }

    public static OrbitCoreOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable(CoreUrlEnv);
        if (string.IsNullOrWhiteSpace(url))
        {
            url = DefaultCoreUrl;
        }

        return new OrbitCoreOptions
        {
            BaseUrl = url.TrimEnd('/'),
            ApiKey = Environment.GetEnvironmentVariable(ApiKeyEnv),
        };
    }
}
