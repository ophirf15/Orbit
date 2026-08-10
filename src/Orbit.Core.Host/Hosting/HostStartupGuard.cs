using Orbit.Core.Host;
using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit.Core.Host.Hosting;

public static class HostStartupGuard
{
    public static HostOptions LoadOptions(JsonOrbitSettingsStore store)
    {
        var settings = store.Load();
        var key = store.ReadCoreHostApiKey(settings);
        var options = new HostOptions
        {
            BindAddress = settings.CoreHostBindAddress,
            Port = TryParsePort(settings.CoreHostBaseUrl) ?? HostOptions.DefaultPort,
            BaseUrl = settings.CoreHostBaseUrl,
            ApiKey = key,
            LocalDataRoot = settings.LocalDataRoot,
            GeneratedFilesRoot = settings.GeneratedFilesRoot,
            CalendarIcsPath = settings.CalendarIcsPath,
            OneDriveSnapshotFolder = settings.OneDriveSnapshotFolder,
            DeviceId = settings.DeviceId,
            DeviceName = settings.DeviceName,
            DeveloperMode = settings.DeveloperMode,
            SourceRepoRoot = settings.SourceRepoRoot,
            DeveloperRemoteOverride = settings.DeveloperRemoteOverride,
            HermesBaseUrl = settings.HermesBaseUrl,
            HermesApiKey = store.ReadHermesApiKey(settings),
            HermesWebhookBaseUrl = settings.HermesWebhookBaseUrl,
            HermesWebhookSecret = TryReadSidecar(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orbit",
                "hermes-webhook-secret.txt")),
        };

        Directory.CreateDirectory(options.LocalDataRoot);
        Directory.CreateDirectory(options.GeneratedFilesRoot);
        return options;
    }

    public static void EnsureMayListen(HostOptions options)
    {
        if (!PathSafety.CanBind(options.BindAddress, options.ApiKey))
        {
            throw new InvalidOperationException(
                $"Refusing to bind Core Host to '{options.BindAddress}' without a Core Host API key. " +
                "Set coreHostApiKeyReference sidecar or bind to 127.0.0.1.");
        }
    }

    private static int? TryParsePort(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return uri.IsDefaultPort ? HostOptions.DefaultPort : uri.Port;
    }

    private static string? TryReadSidecar(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
