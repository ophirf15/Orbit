using Orbit.Core.Settings;

namespace Orbit.Infrastructure.Hermes;

/// <summary>Result of writing a local Hermes Docker bootstrap folder.</summary>
public sealed record HermesLocalBundleResult(
    string Directory,
    string ApiServerKey,
    string DashboardUrl,
    string DashboardUsername,
    string DashboardPassword);

/// <summary>Writes a local Hermes Docker bootstrap folder under the Orbit app data root.</summary>
public static class HermesLocalBundleWriter
{
    public static string GetBundleDirectory(string appRoot) =>
        Path.Combine(appRoot, HermesPairing.LocalBundleFolderName);

    /// <summary>
    /// Creates/overwrites docker-compose.yml + .env (API key + dashboard basic auth).
    /// </summary>
    public static HermesLocalBundleResult Write(
        string appRoot,
        string orbitCoreUrl,
        string? orbitApiKey,
        string? apiServerKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appRoot);

        var dir = GetBundleDirectory(appRoot);
        Directory.CreateDirectory(dir);

        var key = string.IsNullOrWhiteSpace(apiServerKey)
            ? HermesPairing.GenerateApiServerKey()
            : apiServerKey.Trim();
        var dashUser = "orbit";
        var dashPass = HermesPairing.GenerateDashboardPassword();
        var dashSecret = HermesPairing.GenerateApiServerKey();

        File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), HermesPairing.BuildLocalComposeYaml());
        File.WriteAllText(
            Path.Combine(dir, ".env"),
            HermesPairing.BuildLocalEnvFile(key, orbitCoreUrl, orbitApiKey, dashUser, dashPass, dashSecret));

        // Operator-facing credentials (also shown once in Settings).
        File.WriteAllText(
            Path.Combine(dir, "dashboard-login.txt"),
            $"URL: {HermesPairing.LocalDefaultDashboardUrl}\nUsername: {dashUser}\nPassword: {dashPass}\n");

        var readme = """
            Orbit local Hermes bootstrap
            ============================
            Two ports:
              :8642  — OpenAI-compatible API (Orbit Agent chat / Connect & save)
              :9119  — Hermes web dashboard (AI provider login, Telegram, tools)

            First run
            ---------
            1. Install Docker Desktop (Windows).
            2. In this folder: docker compose up -d
               (first boot can take a few minutes while skills sync)
            3. Open http://127.0.0.1:9119 (see dashboard-login.txt) and:
                 - Sign in with the username/password in dashboard-login.txt
                 - Connect your AI provider (API key or OAuth)
                 - Optionally set up Telegram / other messaging
            4. In Orbit Settings, Connect & save against http://127.0.0.1:8642
               (API key already stored when you clicked Prepare).
               If Connect fails while iris SSH -L 8642 is active, stop that
               tunnel first — it steals 127.0.0.1:8642 from local Docker.

            Orbit does not require the browser for dashboard setup — Settings → Open in Orbit
            embeds WebView2 at :9119 (Open in browser remains the OAuth fallback).

            Remote Hermes (home/work): skip this folder. Connect & save with a
            Tailscale/VPN API URL + API_SERVER_KEY, then open the remote
            dashboard at http://<same-host>:9119 if exposed.
            """;
        File.WriteAllText(Path.Combine(dir, "README.txt"), readme.Replace("\r\n", "\n").TrimStart() + "\n");

        // Best-effort: seed SOUL/skills into the compose volume so Docker Hermes is not a blank chatbot.
        try
        {
            var dataHome = Path.Combine(dir, "hermes-data");
            HermesHomeProvisioner.Provision(hermesHome: dataHome, overwriteSoul: false);
        }
        catch
        {
            // non-fatal
        }

        return new HermesLocalBundleResult(
            dir,
            key,
            HermesPairing.LocalDefaultDashboardUrl,
            dashUser,
            dashPass);
    }
}
