using Orbit.Core.Settings;
using Orbit.Infrastructure.Settings;

namespace Orbit_App.Services;

/// <summary>
/// Detects Core Host, optionally starts/restarts it, waits for health, and exposes status for the shell.
/// </summary>
public sealed class CoreHostConnectionService
{
    private const string ProjectBoardFeature = "project-board";
    private const string DutyOperatorFeature = "duty-operator";
    private const string PulseFeature = "pulse";

    private readonly OrbitSettings _settings;
    private readonly JsonOrbitSettingsStore _store;
    private bool _restartAttempted;

    public CoreHostConnectionService(OrbitSettings settings, JsonOrbitSettingsStore store)
    {
        _settings = settings;
        _store = store;
    }

    public CoreHostStatus LastStatus { get; private set; } = new()
    {
        State = CoreHostConnectionState.Unknown,
        Message = "Not checked yet.",
    };

    public async Task<CoreHostStatus> EnsureConnectedAsync(CancellationToken ct = default)
    {
        using var client = new CoreHostClient(_settings, _store);
        if (await client.TryHealthAsync(ct))
        {
            var needsDutyOperator = !await client.HasHealthFeatureAsync(DutyOperatorFeature, ct);
            var needsProjectBoard = !await client.HasHealthFeatureAsync(ProjectBoardFeature, ct);
            var needsPulse = !await client.HasHealthFeatureAsync(PulseFeature, ct);
            if (_settings.BackgroundHostEnabled
                && (needsDutyOperator || needsProjectBoard || needsPulse)
                && !_restartAttempted)
            {
                _restartAttempted = true;
                CoreHostLauncher.TryStopExisting(out var stopDetail);
                CoreHostLauncher.TryStart(_settings, out var launchDetail);
                var require = needsPulse
                    ? PulseFeature
                    : needsDutyOperator
                        ? DutyOperatorFeature
                        : ProjectBoardFeature;
                if (await WaitForHealthyAsync(client, ct, requireFeature: require))
                {
                    return SetConnected(
                        $"Core Host restarted for {(needsPulse ? "Pulse" : needsDutyOperator ? "duty operator" : "project boards")}. {stopDetail} {launchDetail}",
                        await client.TryGetVersionAsync(ct),
                        client.BaseUrl);
                }

                if (await client.TryHealthAsync(ct))
                {
                    return SetConnected(
                        $"Core Host connected (outdated build — Pulse needs a newer Host). {stopDetail} {launchDetail}",
                        await client.TryGetVersionAsync(ct),
                        client.BaseUrl);
                }
            }

            var outdated = !await client.HasHealthFeatureAsync(PulseFeature, ct)
                || !await client.HasHealthFeatureAsync(DutyOperatorFeature, ct);
            return SetConnected(
                outdated
                    ? "Core Host connected (outdated — rebuild/restart Host for Pulse)."
                    : "Core Host connected.",
                await client.TryGetVersionAsync(ct),
                client.BaseUrl);
        }

        if (_settings.BackgroundHostEnabled)
        {
            CoreHostLauncher.TryStopExisting(out _);
            CoreHostLauncher.TryStart(_settings, out var launchDetail);
            if (await WaitForHealthyAsync(client, ct, requireFeature: ProjectBoardFeature))
            {
                return SetConnected(
                    $"Core Host connected after start. {launchDetail}",
                    await client.TryGetVersionAsync(ct),
                    client.BaseUrl);
            }

            // Prefer any healthy host over a blank workbench.
            if (await WaitForHealthyAsync(client, ct))
            {
                return SetConnected(
                    $"Core Host connected after start (outdated — Enter board may fail). {launchDetail}",
                    await client.TryGetVersionAsync(ct),
                    client.BaseUrl);
            }

            LastStatus = new CoreHostStatus
            {
                State = CoreHostConnectionState.Degraded,
                Message = $"Core Host unavailable after start attempt. {launchDetail}",
                BaseUrl = client.BaseUrl,
            };
            return LastStatus;
        }

        LastStatus = new CoreHostStatus
        {
            State = CoreHostConnectionState.Degraded,
            Message = "Core Host unreachable (background host disabled).",
            BaseUrl = client.BaseUrl,
        };
        return LastStatus;
    }

    private CoreHostStatus SetConnected(string message, string? version, string baseUrl)
    {
        LastStatus = new CoreHostStatus
        {
            State = CoreHostConnectionState.Connected,
            Message = message,
            Version = version,
            BaseUrl = baseUrl,
        };
        return LastStatus;
    }

    private static async Task<bool> WaitForHealthyAsync(
        CoreHostClient client,
        CancellationToken ct,
        string? requireFeature = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await client.TryHealthAsync(ct)
                && (requireFeature is null || await client.HasHealthFeatureAsync(requireFeature, ct)))
            {
                return true;
            }

            await Task.Delay(250, ct);
        }

        return false;
    }
}
