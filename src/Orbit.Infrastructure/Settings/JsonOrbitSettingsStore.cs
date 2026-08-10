using System.Text.Json;
using System.Text.Json.Serialization;
using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Settings;

public sealed class JsonOrbitSettingsStore : IOrbitSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath;
    private readonly string _appRoot;

    public JsonOrbitSettingsStore(string? appRoot = null)
    {
        _appRoot = string.IsNullOrWhiteSpace(appRoot)
            ? OrbitSettingsDefaults.DefaultAppRoot
            : appRoot;
        _settingsPath = Path.Combine(_appRoot, OrbitSettingsDefaults.SettingsFileName);
    }

    public string SettingsPath => _settingsPath;

    public string AppRoot => _appRoot;

    public OrbitSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            var created = OrbitSettingsDefaults.CreateDefaults(_appRoot);
            try
            {
                Save(created);
            }
            catch (IOException)
            {
                // First-run create is best-effort; callers still get defaults in memory.
            }

            return created;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            var loaded = JsonSerializer.Deserialize<OrbitSettings>(json, SerializerOptions);
            if (loaded is null)
            {
                return OrbitSettingsDefaults.CreateDefaults(_appRoot);
            }

            Normalize(loaded);
            // Persist generated DeviceId so it stays stable across restarts.
            if (!json.Contains("\"deviceId\"", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(loaded.DeviceId))
            {
                try
                {
                    Save(loaded);
                }
                catch (IOException)
                {
                    // best-effort
                }
            }

            return loaded;
        }
        catch (JsonException)
        {
            return OrbitSettingsDefaults.CreateDefaults(_appRoot);
        }
        catch (IOException)
        {
            return OrbitSettingsDefaults.CreateDefaults(_appRoot);
        }
    }

    public void Save(OrbitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        Directory.CreateDirectory(_appRoot);
        Directory.CreateDirectory(settings.LocalDataRoot);
        Directory.CreateDirectory(settings.GeneratedFilesRoot);

        var payload = CloneWithoutSecrets(settings);
        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        var tempPath = _settingsPath + ".tmp";

        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _settingsPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // best-effort cleanup
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Reads Hermes API key material from the sidecar referenced by settings. Never from settings.json.
    /// </summary>
    public string? ReadHermesApiKey(OrbitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = ResolveKeyPath(settings);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Trim();
    }

    public void WriteHermesApiKey(OrbitSettings settings, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = ResolveKeyPath(settings);
        if (path is null)
        {
            throw new InvalidOperationException("HermesApiKeyReference is not set.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllText(path, apiKey.Trim());
    }

    /// <summary>
    /// Reads Core Host API key material from the sidecar referenced by settings. Never from settings.json.
    /// </summary>
    public string? ReadCoreHostApiKey(OrbitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = ResolveCoreHostKeyPath(settings);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Trim();
    }

    public void WriteCoreHostApiKey(OrbitSettings settings, string? apiKey)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var path = ResolveCoreHostKeyPath(settings);
        if (path is null)
        {
            throw new InvalidOperationException("CoreHostApiKeyReference is not set.");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        File.WriteAllText(path, apiKey.Trim());
    }

    private static OrbitSettings CloneWithoutSecrets(OrbitSettings settings) =>
        new()
        {
            LocalDataRoot = settings.LocalDataRoot,
            GeneratedFilesRoot = settings.GeneratedFilesRoot,
            OneDriveSnapshotFolder = settings.OneDriveSnapshotFolder,
            DeviceId = settings.DeviceId,
            DeviceName = settings.DeviceName,
            HermesBaseUrl = settings.HermesBaseUrl,
            HermesApiKeyReference = settings.HermesApiKeyReference,
            ThemePreference = settings.ThemePreference,
            BackgroundHostEnabled = settings.BackgroundHostEnabled,
            DeveloperMode = settings.DeveloperMode,
            SourceRepoRoot = settings.SourceRepoRoot,
            DeveloperRemoteOverride = settings.DeveloperRemoteOverride,
            CoreHostBaseUrl = settings.CoreHostBaseUrl,
            CoreHostBindAddress = settings.CoreHostBindAddress,
            CoreHostApiKeyReference = settings.CoreHostApiKeyReference,
            CalendarIcsPath = settings.CalendarIcsPath,
            MicrosoftGraphClientId = settings.MicrosoftGraphClientId,
            MicrosoftGraphTenantId = settings.MicrosoftGraphTenantId,
            MicrosoftGraphSignedInUser = settings.MicrosoftGraphSignedInUser,
            WorkbenchCellSize = settings.WorkbenchCellSize,
            UpdatesGitHubOwner = settings.UpdatesGitHubOwner,
            UpdatesGitHubRepo = settings.UpdatesGitHubRepo,
            LastUpdateCheckUtc = settings.LastUpdateCheckUtc,
            LastSeenRemoteVersion = settings.LastSeenRemoteVersion,
        };

    private void Normalize(OrbitSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.LocalDataRoot))
        {
            settings.LocalDataRoot = Path.Combine(_appRoot, "data");
        }

        if (string.IsNullOrWhiteSpace(settings.GeneratedFilesRoot))
        {
            settings.GeneratedFilesRoot = Path.Combine(_appRoot, "generated");
        }

        if (string.IsNullOrWhiteSpace(settings.HermesBaseUrl))
        {
            settings.HermesBaseUrl = OrbitSettingsDefaults.HermesBaseUrl;
        }

        if (!Enum.IsDefined(settings.ThemePreference))
        {
            settings.ThemePreference = ThemePreference.System;
        }

        if (string.IsNullOrWhiteSpace(settings.HermesApiKeyReference))
        {
            settings.HermesApiKeyReference = Path.Combine(_appRoot, OrbitSettingsDefaults.HermesApiKeyFileName);
        }

        if (string.IsNullOrWhiteSpace(settings.CoreHostBaseUrl))
        {
            settings.CoreHostBaseUrl = OrbitSettingsDefaults.CoreHostBaseUrl;
        }
        else
        {
            settings.CoreHostBaseUrl = settings.CoreHostBaseUrl.TrimEnd('/');
        }

        if (string.IsNullOrWhiteSpace(settings.CoreHostBindAddress))
        {
            settings.CoreHostBindAddress = OrbitSettingsDefaults.CoreHostBindAddress;
        }

        if (string.IsNullOrWhiteSpace(settings.CoreHostApiKeyReference))
        {
            settings.CoreHostApiKeyReference = Path.Combine(_appRoot, OrbitSettingsDefaults.CoreHostApiKeyFileName);
        }

        if (string.IsNullOrWhiteSpace(settings.OneDriveSnapshotFolder))
        {
            settings.OneDriveSnapshotFolder = null;
        }

        if (string.IsNullOrWhiteSpace(settings.CalendarIcsPath))
        {
            settings.CalendarIcsPath = null;
        }

        if (string.IsNullOrWhiteSpace(settings.UpdatesGitHubOwner))
        {
            settings.UpdatesGitHubOwner = OrbitSettingsDefaults.UpdatesGitHubOwner;
        }

        if (string.IsNullOrWhiteSpace(settings.UpdatesGitHubRepo))
        {
            settings.UpdatesGitHubRepo = OrbitSettingsDefaults.UpdatesGitHubRepo;
        }

        if (string.IsNullOrWhiteSpace(settings.LastSeenRemoteVersion))
        {
            settings.LastSeenRemoteVersion = null;
        }

        if (string.IsNullOrWhiteSpace(settings.SourceRepoRoot))
        {
            settings.SourceRepoRoot = null;
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceName))
        {
            settings.DeviceName = Environment.MachineName;
        }
    }

    private string? ResolveKeyPath(OrbitSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.HermesApiKeyReference))
        {
            return null;
        }

        return Path.IsPathRooted(settings.HermesApiKeyReference)
            ? settings.HermesApiKeyReference
            : Path.Combine(_appRoot, settings.HermesApiKeyReference);
    }

    private string? ResolveCoreHostKeyPath(OrbitSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CoreHostApiKeyReference))
        {
            return null;
        }

        return Path.IsPathRooted(settings.CoreHostApiKeyReference)
            ? settings.CoreHostApiKeyReference
            : Path.Combine(_appRoot, settings.CoreHostApiKeyReference);
    }
}
