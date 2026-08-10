using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orbit.Core.Updates;

/// <summary>Subset of the GitHub Releases API JSON used by the update checker.</summary>
public sealed class GitHubReleaseDto
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAssetDto>? Assets { get; set; }
}

public sealed class GitHubReleaseAssetDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }
}

public static class GitHubReleaseParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static GitHubReleaseDto Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var dto = JsonSerializer.Deserialize<GitHubReleaseDto>(json, Options);
        return dto ?? throw new JsonException("GitHub release JSON deserialized to null.");
    }

    public static string? FindAssetUrl(GitHubReleaseDto release, params string[] nameSuffixes)
    {
        if (release.Assets is null || release.Assets.Count == 0)
        {
            return null;
        }

        foreach (var suffix in nameSuffixes)
        {
            var match = release.Assets.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.Name)
                && a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
            if (match is not null)
            {
                return match.BrowserDownloadUrl;
            }
        }

        return null;
    }

    /// <summary>Prefers the takeaway wizard asset <c>Orbit-Setup-*.exe</c>.</summary>
    public static string? FindSetupInstallerUrl(GitHubReleaseDto release)
    {
        if (release.Assets is null || release.Assets.Count == 0)
        {
            return null;
        }

        var match = release.Assets.FirstOrDefault(a =>
            !string.IsNullOrWhiteSpace(a.Name)
            && a.Name.StartsWith("Orbit-Setup-", StringComparison.OrdinalIgnoreCase)
            && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl));
        return match?.BrowserDownloadUrl;
    }
}
