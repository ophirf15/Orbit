using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Hermes;

public enum HermesHandshakeState
{
    NotConnected = 0,
    Connecting = 1,
    Connected = 2,
    Failed = 3,
    NotInstalled = 4,
}

public sealed record HermesHandshakeResult(
    HermesHandshakeState State,
    string StatusLine,
    string Detail,
    string? HermesHome,
    string? ApiBaseUrl,
    string? ApiServerKey,
    bool Connected,
    HermesHomeProvisionResult? Provision);

/// <summary>
/// One-shot This-PC Hermes handshake: find install, exchange keys, provision, restart, verify API.
/// </summary>
public static class HermesHandshake
{
    public static async Task<HermesHandshakeResult> ConnectThisPcAsync(
        string orbitCoreUrl,
        string orbitApiKey,
        string? preferredHermesApiKey = null,
        string? docsHermesRoot = null,
        string? orbitMcpCommand = null,
        string? hermesHomeOverride = null,
        bool verifyApi = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orbitCoreUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(orbitApiKey);

        progress?.Report("Looking for Hermes…");
        var install = HermesNativePairer.Detect(hermesHomeOverride);
        if (!install.LooksInstalled)
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.NotInstalled,
                "Hermes not installed",
                $"No native Hermes found at {install.HermesHome}. Install Hermes for Windows, then try again.",
                install.HermesHome,
                null,
                null,
                false,
                null);
        }

        progress?.Report($"Found {install.HermesHome}. Exchanging API keys…");

        // Prefer Hermes' existing API_SERVER_KEY so we don't invalidate a running gateway
        // unless Orbit already has a key and Hermes has none.
        var hermesExisting = HermesEnvFile.Get(install.EnvPath, "API_SERVER_KEY");
        var sharedHermesKey = FirstNonEmpty(hermesExisting, preferredHermesApiKey, HermesPairing.GenerateApiServerKey())!;

        progress?.Report("Writing Hermes .env + SOUL / skills / MCP…");
        // Always refresh SOUL so computer-use bans and MCP guidance stay current.
        var pair = await Task.Run(
                () => HermesNativePairer.Pair(
                    hermesHomeOverride: install.HermesHome,
                    orbitCoreUrl: orbitCoreUrl,
                    orbitApiKey: orbitApiKey,
                    preferredApiServerKey: sharedHermesKey,
                    overwriteSoul: true,
                    docsHermesRoot: docsHermesRoot,
                    orbitMcpCommand: orbitMcpCommand,
                    restartGateway: true),
                cancellationToken)
            .ConfigureAwait(false);

        EnsurePluginMarker(install.HermesHome);

        if (!verifyApi)
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.Connected,
                $"Paired · {pair.ApiBaseUrl} (API verify skipped)",
                pair.Summary,
                pair.HermesHome,
                pair.ApiBaseUrl,
                pair.ApiServerKey,
                true,
                pair.Provision);
        }

        progress?.Report("Waiting for Hermes API…");
        var probe = await WaitForApiAsync(pair.ApiBaseUrl, pair.ApiServerKey, progress, cancellationToken)
            .ConfigureAwait(false);

        if (!probe.Ok)
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.Failed,
                "Handshake incomplete",
                pair.Summary + "\n\nAPI not reachable yet: " + probe.Detail
                + "\nTry again in a few seconds, or run: hermes gateway restart",
                pair.HermesHome,
                pair.ApiBaseUrl,
                pair.ApiServerKey,
                false,
                pair.Provision);
        }

        var skillCount = pair.Provision.SkillsCopied;
        var status =
            $"Connected · {pair.ApiBaseUrl} · skills {skillCount} · MCP {(pair.Provision.McpMerged ? "merged" : "ready")}";

        return new HermesHandshakeResult(
            HermesHandshakeState.Connected,
            status,
            pair.Summary + "\n\nHealth: " + probe.Detail,
            pair.HermesHome,
            pair.ApiBaseUrl,
            pair.ApiServerKey,
            true,
            pair.Provision);
    }

    public static async Task<HermesHandshakeResult> ProbeAsync(
        string? apiBaseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (!HermesUrlValidation.TryValidate(apiBaseUrl, out var url, out var error))
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.NotConnected,
                "Not connected",
                error ?? "Hermes URL missing.",
                null,
                apiBaseUrl,
                null,
                false,
                null);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.NotConnected,
                "Not connected",
                "No Hermes API key stored yet.",
                null,
                url,
                null,
                false,
                null);
        }

        try
        {
            using var client = new HermesHttpClient(new Uri(url!), apiKey);
            var test = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (test.Success)
            {
                return new HermesHandshakeResult(
                    HermesHandshakeState.Connected,
                    $"Connected · {url}",
                    string.Join(
                        "\n",
                        new[] { test.HealthSummary, test.CapabilitiesSummary }
                            .Where(s => !string.IsNullOrWhiteSpace(s))),
                    HermesNativePairer.Detect().LooksInstalled ? HermesNativePairer.ResolveHome() : null,
                    url,
                    apiKey,
                    true,
                    null);
            }

            return new HermesHandshakeResult(
                HermesHandshakeState.Failed,
                "Not connected",
                test.Error ?? "Hermes probe failed.",
                null,
                url,
                apiKey,
                false,
                null);
        }
        catch (Exception ex)
        {
            return new HermesHandshakeResult(
                HermesHandshakeState.Failed,
                "Not connected",
                ex.Message,
                null,
                url,
                apiKey,
                false,
                null);
        }
    }

    private static async Task<(bool Ok, string Detail)> WaitForApiAsync(
        string apiBaseUrl,
        string apiKey,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var delays = new[] { 1, 2, 2, 3, 4, 5, 6, 8 };
        Exception? last = null;
        for (var i = 0; i < delays.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new HermesHttpClient(new Uri(apiBaseUrl), apiKey);
                var health = await client.HealthAsync(cancellationToken).ConfigureAwait(false);
                if (health.Ok)
                {
                    // Confirm key is accepted when capabilities/models require auth.
                    var test = await client.TestConnectionAsync(cancellationToken).ConfigureAwait(false);
                    if (test.Success)
                    {
                        return (true, test.HealthSummary ?? "ok");
                    }

                    // Health OK but key rejected — still report failure so UI can reconnect.
                    if (!string.IsNullOrWhiteSpace(test.Error)
                        && test.Error.Contains("API key", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, test.Error);
                    }

                    // Degraded: health ok is enough for local handshake.
                    return (true, health.RawBody ?? $"HTTP {health.StatusCode}");
                }

                last = new InvalidOperationException($"HTTP {health.StatusCode}: {health.RawBody}");
            }
            catch (Exception ex)
            {
                last = ex;
            }

            var wait = delays[i];
            progress?.Report($"Waiting for Hermes API… ({i + 1}/{delays.Length})");
            await Task.Delay(TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false);
        }

        return (false, last?.Message ?? "timed out waiting for :8642");
    }

    private static void EnsurePluginMarker(string hermesHome)
    {
        try
        {
            var dir = Path.Combine(hermesHome, "plugins", "orbit");
            Directory.CreateDirectory(dir);
            var readme = Path.Combine(dir, "README.md");
            if (!File.Exists(readme))
            {
                File.WriteAllText(
                    readme,
                    "# Orbit ↔ Hermes\n\n"
                    + "Orbit integrates via **MCP + skills + SOUL**, not a Hermes Python plugin.\n"
                    + "Tools come from `mcp_servers.orbit` in config.yaml.\n");
            }
        }
        catch
        {
            // non-fatal
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }
}
