namespace Orbit.Core.Host;

/// <summary>
/// Listen/bind options for Orbit.Core.Host. Defaults favor loopback-only exposure.
/// </summary>
public sealed class HostOptions
{
    public const string DefaultBindAddress = "127.0.0.1";

    public const int DefaultPort = 8741;

    public const string DefaultBaseUrl = "http://127.0.0.1:8741";

    public string BindAddress { get; set; } = DefaultBindAddress;

    public int Port { get; set; } = DefaultPort;

    /// <summary>Optional override; when empty, derived from bind + port.</summary>
    public string? BaseUrl { get; set; }

    public string? ApiKey { get; set; }

    public string LocalDataRoot { get; set; } = string.Empty;

    public string GeneratedFilesRoot { get; set; } = string.Empty;

    /// <summary>Optional ICS file path or URL loaded from settings for sync.</summary>
    public string? CalendarIcsPath { get; set; }

    /// <summary>User-selected OneDrive (or any) snapshot sync folder; optional.</summary>
    public string? OneDriveSnapshotFolder { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Mirrors OrbitSettings.DeveloperMode for Host-side developer tools.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Configured Orbit source repo root; required for developer/source tools.</summary>
    public string? SourceRepoRoot { get; set; }

    /// <summary>When true, Telegram may invoke developer tools (default false).</summary>
    public bool DeveloperRemoteOverride { get; set; }

    /// <summary>Optional Hermes API base URL for operator wake (from settings).</summary>
    public string? HermesBaseUrl { get; set; }

    /// <summary>Optional Hermes API key for operator wake (from sidecar).</summary>
    public string? HermesApiKey { get; set; }

    /// <summary>Optional Hermes webhook base (default derived as API host :8644).</summary>
    public string? HermesWebhookBaseUrl { get; set; }

    /// <summary>HMAC secret for Hermes webhook routes (orbit-email-ingested).</summary>
    public string? HermesWebhookSecret { get; set; }

    public string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl.TrimEnd('/');
        }

        return $"http://{BindAddress}:{Port}";
    }
}
