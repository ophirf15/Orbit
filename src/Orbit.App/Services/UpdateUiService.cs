using System.Diagnostics;
using Orbit.Core.Settings;
using Orbit.Core.Updates;
using Orbit.Infrastructure.Settings;
using Orbit.Infrastructure.Updates;

namespace Orbit_App.Services;

/// <summary>Shared About/Settings update-check wiring against GitHub Releases.</summary>
public static class UpdateUiService
{
    public static async Task<UpdateCheckResult> CheckAndPersistAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        using var checker = new GitHubReleasesUpdateChecker(
            settings.UpdatesGitHubOwner,
            settings.UpdatesGitHubRepo);

        var current = AppVersion.GetUpdateCompareVersion();
        var result = await checker.CheckAsync(current, cancellationToken).ConfigureAwait(false);

        settings.LastUpdateCheckUtc = result.CheckedAtUtc;
        if (!string.IsNullOrWhiteSpace(result.RemoteVersion))
        {
            settings.LastSeenRemoteVersion = result.RemoteVersion;
        }

        try
        {
            store.Save(settings);
        }
        catch (IOException)
        {
            // Best-effort; UI still shows the check result.
        }

        return result;
    }

    public static async Task<PreUpdateSnapshotResult> SnapshotBeforeApplyAsync(
        OrbitSettings settings,
        JsonOrbitSettingsStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        return await PreUpdateSnapshotGuard.EnsureAsync(
            settings.OneDriveSnapshotFolder,
            async ct =>
            {
                using var client = new CoreHostClient(settings, store);
                return await client.CreateSyncSnapshotAsync(ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies an update: prefer downloading <c>Orbit-Setup-*.exe</c> and launching a silent Inno upgrade;
    /// otherwise open App Installer / MSIX / release page in the shell.
    /// </summary>
    public static async Task<(bool Ok, string Message)> ApplyUpdateAsync(
        UpdateCheckResult result,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(result.SetupInstallerUrl))
        {
            using var applier = new OrbitSetupUpdateApplier();
            var launch = await applier.DownloadAndLaunchAsync(result.SetupInstallerUrl, cancellationToken)
                .ConfigureAwait(false);
            if (launch.Ok)
            {
                // Give elevated setup a moment to start, then exit so Program Files unlocks.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500).ConfigureAwait(false);
                        OrbitProcessShutdown.KillOrbitRelated(TimeSpan.FromMilliseconds(500));
                        Environment.Exit(0);
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }
                });
            }

            return launch;
        }

        var uri = result.AppInstallerUrl
                  ?? result.MsixAssetUrl
                  ?? result.ReleaseHtmlUrl;
        if (string.IsNullOrWhiteSpace(uri))
        {
            return (false, "No Orbit-Setup.exe, App Installer, MSIX, or release URL on the latest GitHub release.");
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return (true, "Opened update location in the browser / App Installer.");
        }
        catch (Exception ex)
        {
            return (false, "Could not open update URL: " + ex.Message);
        }
    }

    public static string FormatStatus(OrbitSettings settings, UpdateCheckResult? last = null)
    {
        var lines = new List<string>
        {
            $"Current version: {AppVersion.GetInformationalVersion()} (compare {AppVersion.GetUpdateCompareVersion()})",
        };

        if (settings.LastUpdateCheckUtc is { } lastCheck)
        {
            lines.Add($"Last check: {lastCheck.LocalDateTime:g} (local)");
        }
        else
        {
            lines.Add("Last check: never");
        }

        if (!string.IsNullOrWhiteSpace(settings.LastSeenRemoteVersion))
        {
            lines.Add($"Last seen remote: {settings.LastSeenRemoteVersion}");
        }

        if (last is null)
        {
            return string.Join(Environment.NewLine, lines);
        }

        if (!last.Succeeded)
        {
            lines.Add("Check error: " + last.Error);
            return string.Join(Environment.NewLine, lines);
        }

        if (last.UpdateAvailable)
        {
            lines.Add($"Update available: {last.RemoteVersion}");
            if (!string.IsNullOrWhiteSpace(last.SetupInstallerUrl))
            {
                lines.Add("Installer: Orbit-Setup (GitHub)");
            }

            if (!string.IsNullOrWhiteSpace(last.ReleaseHtmlUrl))
            {
                lines.Add("Notes: " + last.ReleaseHtmlUrl);
            }
        }
        else
        {
            lines.Add($"Up to date (remote {last.RemoteVersion ?? "n/a"}).");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
