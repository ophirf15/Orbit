using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Hermes;

public sealed record HermesNativeInstallInfo(
    string HermesHome,
    string EnvPath,
    string? HermesExePath,
    bool LooksInstalled);

public sealed record HermesNativePairResult(
    string HermesHome,
    string ApiBaseUrl,
    string ApiServerKey,
    bool EnvChanged,
    bool ApiServerEnabledWritten,
    bool OrbitEnvWritten,
    bool GatewayRestartAttempted,
    bool GatewayRestartOk,
    string? GatewayRestartDetail,
    HermesHomeProvisionResult Provision,
    string Summary);

/// <summary>
/// Locates a native Windows Hermes install (%LOCALAPPDATA%\hermes) and pairs it with Orbit:
/// upsert Hermes .env (API_SERVER_* + ORBIT_*), provision SOUL/skills/MCP, return Orbit connect values.
/// </summary>
public static class HermesNativePairer
{
    public static HermesNativeInstallInfo Detect(string? hermesHomeOverride = null)
    {
        var home = ResolveHome(hermesHomeOverride);
        var envPath = Path.Combine(home, ".env");
        var exe = ResolveHermesExe(home);
        var looksInstalled =
            Directory.Exists(home)
            && (File.Exists(envPath)
                || Directory.Exists(Path.Combine(home, "hermes-agent"))
                || File.Exists(Path.Combine(home, "config.yaml"))
                || exe is not null);

        return new HermesNativeInstallInfo(home, envPath, exe, looksInstalled);
    }

    public static HermesNativePairResult Pair(
        string? hermesHomeOverride = null,
        string? orbitCoreUrl = null,
        string? orbitApiKey = null,
        string? preferredApiServerKey = null,
        bool overwriteSoul = false,
        string? docsHermesRoot = null,
        string? orbitMcpCommand = null,
        bool restartGateway = true)
    {
        var install = Detect(hermesHomeOverride);
        if (!install.LooksInstalled && !Directory.Exists(install.HermesHome))
        {
            Directory.CreateDirectory(install.HermesHome);
        }

        var existing = HermesEnvFile.Read(install.EnvPath);
        var apiKey = FirstNonEmpty(
            preferredApiServerKey,
            GetMap(existing, "API_SERVER_KEY"),
            HermesPairing.GenerateApiServerKey())!;

        var portRaw = FirstNonEmpty(GetMap(existing, "API_SERVER_PORT"), "8642")!;
        if (!int.TryParse(portRaw, out var port) || port <= 0 || port > 65535)
        {
            port = HermesPairing.ApiPort;
        }

        var host = FirstNonEmpty(GetMap(existing, "API_SERVER_HOST"), "127.0.0.1")!;
        // Orbit always talks loopback when Hermes is local; keep Hermes bind as configured
        // but expose a loopback URL to the app when host is 0.0.0.0.
        var urlHost = string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "*", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : host;
        var apiBaseUrl = $"http://{urlHost}:{port}";

        var coreUrl = string.IsNullOrWhiteSpace(orbitCoreUrl)
            ? "http://127.0.0.1:8741"
            : orbitCoreUrl.Trim().TrimEnd('/');
        var coreKey = string.IsNullOrWhiteSpace(orbitApiKey) ? string.Empty : orbitApiKey.Trim();

        var upsert = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["API_SERVER_ENABLED"] = "true",
            ["API_SERVER_HOST"] = host is "0.0.0.0" or "*" or "::" ? host : "127.0.0.1",
            ["API_SERVER_PORT"] = port.ToString(),
            ["API_SERVER_KEY"] = apiKey,
            ["ORBIT_CORE_URL"] = coreUrl,
        };
        if (!string.IsNullOrWhiteSpace(coreKey))
        {
            upsert["ORBIT_API_KEY"] = coreKey;
        }

        // Prefer binding API to loopback for local native installs unless already LAN-bound.
        if (string.IsNullOrWhiteSpace(GetMap(existing, "API_SERVER_HOST"))
            || string.Equals(GetMap(existing, "API_SERVER_HOST"), "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            upsert["API_SERVER_HOST"] = "127.0.0.1";
            apiBaseUrl = $"http://127.0.0.1:{port}";
        }

        var envChanged = HermesEnvFile.Upsert(install.EnvPath, upsert);
        var apiEnabledWritten = !string.Equals(
            GetMap(existing, "API_SERVER_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var orbitEnvWritten =
            !string.Equals(GetMap(existing, "ORBIT_CORE_URL"), coreUrl, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(coreKey)
                && !string.Equals(GetMap(existing, "ORBIT_API_KEY"), coreKey, StringComparison.Ordinal));

        var provision = HermesHomeProvisioner.Provision(
            hermesHome: install.HermesHome,
            overwriteSoul: overwriteSoul,
            docsHermesRoot: docsHermesRoot,
            orbitMcpCommand: orbitMcpCommand,
            orbitCoreUrl: coreUrl,
            orbitApiKey: coreKey);

        var gatewayRestartAttempted = false;
        var gatewayRestartOk = false;
        string? gatewayDetail = null;
        if (restartGateway)
        {
            gatewayRestartAttempted = true;
            (gatewayRestartOk, gatewayDetail) = TryRestartGateway(install);
        }

        var summary = BuildSummary(
            install,
            apiBaseUrl,
            envChanged,
            apiEnabledWritten,
            orbitEnvWritten,
            gatewayRestartAttempted,
            gatewayRestartOk,
            gatewayDetail,
            provision);

        return new HermesNativePairResult(
            install.HermesHome,
            apiBaseUrl,
            apiKey,
            envChanged,
            apiEnabledWritten,
            orbitEnvWritten,
            gatewayRestartAttempted,
            gatewayRestartOk,
            gatewayDetail,
            provision,
            summary);
    }

    public static string ResolveHome(string? hermesHomeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(hermesHomeOverride))
        {
            return hermesHomeOverride.Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable("HERMES_HOME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return HermesHomeProvisioner.DefaultHermesHome;
    }

    public static string? ResolveHermesExe(string hermesHome)
    {
        var candidates = new[]
        {
            Path.Combine(hermesHome, "hermes-agent", "venv", "Scripts", "hermes.exe"),
            Path.Combine(hermesHome, "hermes-agent", "venv", "bin", "hermes"),
            Path.Combine(hermesHome, "bin", "hermes.exe"),
            Path.Combine(hermesHome, "bin", "hermes"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
        }

        // PATH lookup (may be missing in a fresh shell before PATH refresh).
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var exe = Path.Combine(dir.Trim(), OperatingSystem.IsWindows() ? "hermes.exe" : "hermes");
                if (File.Exists(exe))
                {
                    return exe;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static (bool Ok, string Detail) TryRestartGateway(HermesNativeInstallInfo install)
    {
        var exe = install.HermesExePath ?? ResolveHermesExe(install.HermesHome);
        if (exe is null)
        {
            return (false, "hermes.exe not found — restart gateway manually after PATH refresh.");
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = "gateway restart",
                WorkingDirectory = install.HermesHome,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["HERMES_HOME"] = install.HermesHome;

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                return (false, "Failed to start hermes gateway restart.");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(90_000))
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return (false, "hermes gateway restart timed out.");
            }

            var detail = string.Join(
                "\n",
                new[] { stdout.Trim(), stderr.Trim() }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (proc.ExitCode != 0)
            {
                return (false, string.IsNullOrWhiteSpace(detail)
                    ? $"hermes gateway restart exited {proc.ExitCode}."
                    : detail);
            }

            return (true, string.IsNullOrWhiteSpace(detail) ? "gateway restarted." : detail);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildSummary(
        HermesNativeInstallInfo install,
        string apiBaseUrl,
        bool envChanged,
        bool apiEnabledWritten,
        bool orbitEnvWritten,
        bool gatewayRestartAttempted,
        bool gatewayRestartOk,
        string? gatewayDetail,
        HermesHomeProvisionResult provision)
    {
        var lines = new List<string>
        {
            $"Hermes home: {install.HermesHome}",
            $"API URL: {apiBaseUrl}",
            envChanged ? "Hermes .env updated (API_SERVER_* / ORBIT_*)." : "Hermes .env already had Orbit pairing keys.",
            $"SOUL wrote: {provision.SoulWrote}; skills: {provision.SkillsCopied}; flat collisions quarantined: {provision.FlatSkillsQuarantined}; scripts: {provision.ScriptsCopied}; cron jobs: {provision.CronJobsApplied}; webhooks: {provision.WebhooksConfigured}; MCP merged: {provision.McpMerged}",
        };

        if (apiEnabledWritten)
        {
            lines.Add("Enabled API_SERVER_ENABLED=true (required for :8642).");
        }

        if (orbitEnvWritten)
        {
            lines.Add("Wrote ORBIT_CORE_URL / ORBIT_API_KEY into Hermes .env.");
        }

        if (gatewayRestartAttempted)
        {
            lines.Add(gatewayRestartOk
                ? "Gateway restart: ok."
                : "Gateway restart: " + (gatewayDetail ?? "failed — run: hermes gateway restart"));
        }
        else
        {
            lines.Add("Restart Hermes gateway so it picks up .env, then Connect & save.");
        }

        if (!string.IsNullOrWhiteSpace(provision.Note))
        {
            lines.Add(provision.Note);
        }

        return string.Join("\n", lines);
    }

    private static string? GetMap(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

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
