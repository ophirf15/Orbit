namespace Orbit.Core.Updates;

/// <summary>
/// Discovers newer Orbit builds from a public GitHub Releases feed.
/// Does not download or install packages — App Installer owns apply for the MSIX lane.
/// </summary>
public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default);
}
