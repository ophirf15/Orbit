using System.Net.Http.Headers;
using System.Text.Json;
using Orbit.Core.Updates;

namespace Orbit.Infrastructure.Updates;

public sealed class GitHubReleasesUpdateChecker : IUpdateChecker, IDisposable
{
    public const string DefaultOwner = "ophirf15";
    public const string DefaultRepo = "Orbit";
    public const string DefaultApiBase = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubReleasesUpdateChecker(
        string? owner = null,
        string? repo = null,
        Uri? apiBase = null)
        : this(new HttpClientHandler(), owner, repo, apiBase, disposeHandler: true)
    {
    }

    /// <summary>Test-friendly constructor; handler is not disposed.</summary>
    public GitHubReleasesUpdateChecker(
        HttpMessageHandler handler,
        string? owner = null,
        string? repo = null,
        Uri? apiBase = null)
        : this(handler, owner, repo, apiBase, disposeHandler: false)
    {
    }

    private GitHubReleasesUpdateChecker(
        HttpMessageHandler handler,
        string? owner,
        string? repo,
        Uri? apiBase,
        bool disposeHandler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        _owner = string.IsNullOrWhiteSpace(owner) ? DefaultOwner : owner.Trim();
        _repo = string.IsNullOrWhiteSpace(repo) ? DefaultRepo : repo.Trim();
        _ownsHttp = true;
        _http = new HttpClient(handler, disposeHandler)
        {
            BaseAddress = apiBase ?? new Uri(DefaultApiBase + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Orbit", "1.0"));
        }

        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        var checkedAt = DateTimeOffset.UtcNow;
        var path = $"/repos/{_owner}/{_repo}/releases/latest";

        try
        {
            using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = currentVersion,
                    Error = $"GitHub Releases HTTP {(int)response.StatusCode}: {Truncate(body, 400)}",
                };
            }

            var release = GitHubReleaseParser.Parse(body);
            if (release.Draft)
            {
                return new UpdateCheckResult
                {
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = currentVersion,
                    Error = "Latest GitHub release is a draft.",
                };
            }

            var remote = release.TagName ?? release.Name;
            if (string.IsNullOrWhiteSpace(remote))
            {
                return new UpdateCheckResult
                {
                    CheckedAtUtc = checkedAt,
                    CurrentVersion = currentVersion,
                    Error = "Latest release has no tag_name.",
                };
            }

            var setup = GitHubReleaseParser.FindSetupInstallerUrl(release);
            var appInstaller = GitHubReleaseParser.FindAssetUrl(release, ".appinstaller");
            var msix = GitHubReleaseParser.FindAssetUrl(release, ".msix", ".msixbundle");

            return new UpdateCheckResult
            {
                CheckedAtUtc = checkedAt,
                CurrentVersion = currentVersion,
                RemoteVersion = remote,
                UpdateAvailable = SemVer.IsNewer(remote, currentVersion),
                ReleaseNotes = string.IsNullOrWhiteSpace(release.Body) ? null : release.Body.Trim(),
                ReleaseHtmlUrl = release.HtmlUrl,
                SetupInstallerUrl = setup,
                AppInstallerUrl = appInstaller,
                MsixAssetUrl = msix,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult
            {
                CheckedAtUtc = checkedAt,
                CurrentVersion = currentVersion,
                Error = "Failed to parse GitHub release JSON: " + ex.Message,
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                CheckedAtUtc = checkedAt,
                CurrentVersion = currentVersion,
                Error = "Update check failed: " + ex.Message,
            };
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
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
