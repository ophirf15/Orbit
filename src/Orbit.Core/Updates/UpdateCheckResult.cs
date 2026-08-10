namespace Orbit.Core.Updates;

public sealed class UpdateCheckResult
{
    public required DateTimeOffset CheckedAtUtc { get; init; }

    public required string CurrentVersion { get; init; }

    public string? RemoteVersion { get; init; }

    public bool UpdateAvailable { get; init; }

    public string? ReleaseNotes { get; init; }

    public string? ReleaseHtmlUrl { get; init; }

    public string? AppInstallerUrl { get; init; }

    public string? MsixAssetUrl { get; init; }

    /// <summary>GitHub asset URL for the Inno wizard installer (<c>Orbit-Setup-*.exe</c>).</summary>
    public string? SetupInstallerUrl { get; init; }

    public string? Error { get; init; }

    public bool Succeeded => string.IsNullOrWhiteSpace(Error);
}

public sealed class PreUpdateSnapshotResult
{
    public bool Attempted { get; init; }

    public bool SkippedBecauseUnset { get; init; }

    public bool Succeeded { get; init; }

    public string Message { get; init; } = string.Empty;
}
