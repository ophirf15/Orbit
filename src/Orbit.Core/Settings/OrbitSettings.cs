namespace Orbit.Core.Settings;

/// <summary>
/// Theme preference for the desktop shell. Full Fluent chrome arrives in Phase 2.
/// </summary>
public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// How Orbit pairs with Hermes in Settings.
/// ThisPc = one-click native install handshake; Manual = URL + API key.
/// </summary>
public enum HermesConnectMode
{
    ThisPc = 0,
    Manual = 1,
}

/// <summary>
/// Typed Orbit settings. Secrets are never stored on this object — only a key reference path.
/// </summary>
public sealed class OrbitSettings
{
    public string LocalDataRoot { get; set; } = string.Empty;

    public string GeneratedFilesRoot { get; set; } = string.Empty;

    /// <summary>User-selected OneDrive snapshot folder; null/empty until configured.</summary>
    public string? OneDriveSnapshotFolder { get; set; }

    /// <summary>Stable device id for snapshot lineage; generated once and persisted.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Human-readable device label stored on snapshot manifests.</summary>
    public string DeviceName { get; set; } = string.Empty;

    public string HermesBaseUrl { get; set; } = OrbitSettingsDefaults.HermesBaseUrl;

    /// <summary>
    /// ThisPc hides URL/key fields and uses Detect &amp; pair; Manual shows them for remote Hermes.
    /// </summary>
    public HermesConnectMode HermesConnectMode { get; set; } = HermesConnectMode.ThisPc;

    /// <summary>
    /// Path to a local sidecar file holding the Hermes API key, relative to <see cref="LocalDataRoot"/>
    /// or absolute. The key material itself must not live in settings.json.
    /// </summary>
    public string? HermesApiKeyReference { get; set; }

    /// <summary>Optional override for Hermes webhook base (default: Hermes API host on port 8644).</summary>
    public string? HermesWebhookBaseUrl { get; set; }

    public ThemePreference ThemePreference { get; set; } = ThemePreference.System;

    public bool BackgroundHostEnabled { get; set; } = true;

    public bool DeveloperMode { get; set; }

    /// <summary>
    /// Absolute path to the Orbit source repository/worktree used by Developer Mode tools.
    /// Never a project folder path.
    /// </summary>
    public string? SourceRepoRoot { get; set; }

    /// <summary>
    /// When false (default), Telegram provenance cannot invoke developer/source tools.
    /// </summary>
    public bool DeveloperRemoteOverride { get; set; }

    /// <summary>Base URL the desktop App uses to reach Core Host (typically loopback).</summary>
    public string CoreHostBaseUrl { get; set; } = OrbitSettingsDefaults.CoreHostBaseUrl;

    /// <summary>Address Core Host binds to. Loopback by default; LAN requires an API key.</summary>
    public string CoreHostBindAddress { get; set; } = OrbitSettingsDefaults.CoreHostBindAddress;

    /// <summary>
    /// Path to a local sidecar file holding the Core Host API key, relative to LocalAppData Orbit root
    /// or absolute. Required when bind is non-loopback.
    /// </summary>
    public string? CoreHostApiKeyReference { get; set; }

    /// <summary>Optional ICS file path or HTTP(S) URL for calendar sync.</summary>
    public string? CalendarIcsPath { get; set; }

    /// <summary>Optional Entra public-client application ID for Outlook-style Graph login.</summary>
    public string? MicrosoftGraphClientId { get; set; }

    /// <summary>Tenant: "common" (default), "organizations", "consumers", or a tenant GUID.</summary>
    public string MicrosoftGraphTenantId { get; set; } = "common";

    /// <summary>Cached signed-in Microsoft account username (display only; tokens live in MSAL cache).</summary>
    public string? MicrosoftGraphSignedInUser { get; set; }

    /// <summary>Workbench cell size: 0=compact, 1=comfortable (default), 2=large.</summary>
    public int WorkbenchCellSize { get; set; } = 1;

    /// <summary>GitHub owner for public Releases update checks (Phase 17).</summary>
    public string UpdatesGitHubOwner { get; set; } = OrbitSettingsDefaults.UpdatesGitHubOwner;

    /// <summary>GitHub repo for public Releases update checks (Phase 17).</summary>
    public string UpdatesGitHubRepo { get; set; } = OrbitSettingsDefaults.UpdatesGitHubRepo;

    /// <summary>UTC timestamp of the last successful or attempted update check.</summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    /// <summary>Remote tag seen on the last check (informational).</summary>
    public string? LastSeenRemoteVersion { get; set; }
}

public static class OrbitSettingsDefaults
{
    public const string HermesBaseUrl = "http://127.0.0.1:8642";

    public const string HermesApiKeyFileName = "hermes-api-key.txt";

    public const string CoreHostBaseUrl = "http://127.0.0.1:8741";

    public const string CoreHostBindAddress = "127.0.0.1";

    public const string CoreHostApiKeyFileName = "core-host-api-key.txt";

    public const string SettingsFileName = "settings.json";

    public const string UpdatesGitHubOwner = "ophirf15";

    public const string UpdatesGitHubRepo = "Orbit";

    public static string DefaultAppRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit");

    public static OrbitSettings CreateDefaults(string? appRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(appRoot) ? DefaultAppRoot : appRoot;
        return new OrbitSettings
        {
            LocalDataRoot = Path.Combine(root, "data"),
            GeneratedFilesRoot = Path.Combine(root, "generated"),
            OneDriveSnapshotFolder = null,
            DeviceId = Guid.NewGuid().ToString("N"),
            DeviceName = Environment.MachineName,
            HermesBaseUrl = HermesBaseUrl,
            HermesConnectMode = HermesConnectMode.ThisPc,
            HermesApiKeyReference = Path.Combine(root, HermesApiKeyFileName),
            ThemePreference = ThemePreference.System,
            BackgroundHostEnabled = true,
            DeveloperMode = false,
            SourceRepoRoot = null,
            DeveloperRemoteOverride = false,
            CoreHostBaseUrl = CoreHostBaseUrl,
            CoreHostBindAddress = CoreHostBindAddress,
            CoreHostApiKeyReference = Path.Combine(root, CoreHostApiKeyFileName),
            MicrosoftGraphClientId = null,
            MicrosoftGraphTenantId = "common",
            MicrosoftGraphSignedInUser = null,
            WorkbenchCellSize = 1,
            UpdatesGitHubOwner = UpdatesGitHubOwner,
            UpdatesGitHubRepo = UpdatesGitHubRepo,
            LastUpdateCheckUtc = null,
            LastSeenRemoteVersion = null,
        };
    }
}
