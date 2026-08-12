using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Orbit.Infrastructure.Diagnostics;

namespace Orbit.Infrastructure.Updates;

/// <summary>
/// Downloads the GitHub-hosted Inno <c>Orbit-Setup-*.exe</c> and schedules an elevated
/// in-place upgrade that starts only after Orbit.App has fully exited (avoids UAC cancel
/// when the requesting process dies, and avoids /CLOSEAPPLICATIONS racing the App).
/// </summary>
public sealed class OrbitSetupUpdateApplier : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public OrbitSetupUpdateApplier()
        : this(new HttpClientHandler { AllowAutoRedirect = true }, disposeHandler: true)
    {
    }

    public OrbitSetupUpdateApplier(HttpMessageHandler handler, bool disposeHandler = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _ownsHttp = true;
        _http = new HttpClient(handler, disposeHandler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Orbit", "1.0"));
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    public static bool IsTrustedReleaseAssetUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
               || host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<(bool Ok, string Message)> DownloadAndLaunchAsync(
        string setupUrl,
        CancellationToken cancellationToken = default)
    {
        if (!IsTrustedReleaseAssetUrl(setupUrl))
        {
            return (false, "Setup URL is not a trusted GitHub release asset host.");
        }

        var uri = new Uri(setupUrl);
        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)
            || !fileName.StartsWith("Orbit-Setup-", StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "Orbit-Setup-update.exe";
        }

        var dir = Path.Combine(Path.GetTempPath(), "OrbitUpdates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, fileName);

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return (false, $"Download failed HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            var final = response.RequestMessage?.RequestUri ?? uri;
            if (!IsTrustedReleaseAssetUrl(final.ToString()))
            {
                return (false, "Download redirected to an untrusted host.");
            }

            await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            MotwUnblocker.UnblockFile(target);

            // Do NOT elevate from this process and then Exit — Windows cancels pending UAC
            // when the requester dies, which looks like "app and installer both force-closed".
            // Schedule a detached helper that waits for Orbit.App to exit, then RunAs setup.
            if (!TryScheduleDeferredElevatedSetup(target, out var scheduleError))
            {
                return (false, scheduleError);
            }

            return (
                true,
                "Update queued. Orbit will close; approve the UAC prompt when it appears, then reopen Orbit from Start.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, "Could not download or launch setup: " + ex.Message);
        }
    }

    /// <summary>
    /// Writes a PowerShell helper under %TEMP%\OrbitUpdates and starts it detached.
    /// The helper waits until Orbit.App is gone, kills Host/MCP, then elevates setup.
    /// </summary>
    public static bool TryScheduleDeferredElevatedSetup(string setupExePath, out string error)
    {
        error = string.Empty;
        try
        {
            if (!File.Exists(setupExePath))
            {
                error = "Setup EXE missing after download.";
                return false;
            }

            var dir = Path.GetDirectoryName(setupExePath)
                      ?? Path.Combine(Path.GetTempPath(), "OrbitUpdates");
            Directory.CreateDirectory(dir);

            var helperPs1 = Path.Combine(dir, "orbit-run-update.ps1");
            var logPath = Path.Combine(dir, "orbit-update-helper.log");

            // No /CLOSEAPPLICATIONS — App is already gone; Inno PrepareToInstall kills Host/MCP.
            const string setupArgs = "/SILENT /NORESTART /SUPPRESSMSGBOXES";

            var ps = new StringBuilder();
            ps.AppendLine("$ErrorActionPreference = 'Stop'");
            ps.AppendLine("$log = " + PsSingleQuoted(logPath));
            ps.AppendLine("$setup = " + PsSingleQuoted(setupExePath));
            ps.AppendLine("$setupArgs = " + PsSingleQuoted(setupArgs));
            ps.AppendLine("function Write-Log([string] $msg) {");
            ps.AppendLine("  Add-Content -LiteralPath $log -Value ('[' + (Get-Date -Format o) + '] ' + $msg)");
            ps.AppendLine("}");
            ps.AppendLine("Write-Log 'helper start'");
            ps.AppendLine("while (Get-Process -Name 'Orbit.App' -ErrorAction SilentlyContinue) {");
            ps.AppendLine("  Start-Sleep -Seconds 1");
            ps.AppendLine("}");
            ps.AppendLine("Write-Log 'Orbit.App exited'");
            ps.AppendLine("Start-Sleep -Seconds 2");
            ps.AppendLine("foreach ($n in @('Orbit.Core.Host','Orbit.Mcp')) {");
            ps.AppendLine("  Get-Process -Name $n -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue");
            ps.AppendLine("}");
            ps.AppendLine("Start-Sleep -Seconds 1");
            ps.AppendLine("try {");
            ps.AppendLine("  Write-Log 'elevating setup'");
            ps.AppendLine("  Start-Process -FilePath $setup -ArgumentList $setupArgs -Verb RunAs");
            ps.AppendLine("  Write-Log 'Start-Process returned'");
            ps.AppendLine("} catch {");
            ps.AppendLine("  Write-Log ('ERROR: ' + $_.Exception.Message)");
            ps.AppendLine("  exit 1");
            ps.AppendLine("}");
            File.WriteAllText(helperPs1, ps.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var start = new ProcessStartInfo
            {
                // `start` breaks away from Orbit.App's process tree / job so Exit won't cancel UAC.
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c start \"OrbitUpdate\" /MIN \""
                            + Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
                            + "\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""
                            + helperPs1
                            + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var proc = Process.Start(start);
            if (proc is null)
            {
                error = "Could not start the deferred update helper.";
                return false;
            }

            // Let cmd spawn the breakaway powershell, then return.
            proc.WaitForExit(8_000);

            OrbitSupportLog.Write("Update", "Deferred update helper scheduled: " + helperPs1);
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not schedule update helper: " + ex.Message;
            return false;
        }
    }

    private static string PsSingleQuoted(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "…";
    }
}
