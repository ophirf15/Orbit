using System.Diagnostics;
using System.Net.Http.Headers;

namespace Orbit.Infrastructure.Updates;

/// <summary>
/// Downloads the GitHub-hosted Inno <c>Orbit-Setup-*.exe</c> and launches a silent in-place upgrade
/// (same AppId — replaces Program Files without uninstall).
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

            // Redirects may land on the GitHub CDN; reject unexpected final hosts.
            var final = response.RequestMessage?.RequestUri ?? uri;
            if (!IsTrustedReleaseAssetUrl(final.ToString()))
            {
                return (false, "Download redirected to an untrusted host.");
            }

            await using (var fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var start = new ProcessStartInfo
            {
                FileName = target,
                // Same AppId → in-place upgrade. CloseApplications in Orbit.iss stops Orbit first.
                Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART /SUPPRESSMSGBOXES",
                UseShellExecute = true,
            };

            Process.Start(start);
            return (
                true,
                "Downloading finished. Setup is running a silent upgrade — approve UAC if prompted. Orbit will close briefly and reopen from Start when done.");
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

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text;
        }

        return text[..max] + "…";
    }
}
