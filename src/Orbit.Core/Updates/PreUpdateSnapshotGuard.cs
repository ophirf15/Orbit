namespace Orbit.Core.Updates;

/// <summary>
/// Ensures a OneDrive DB snapshot runs before applying an update when a sync folder is configured.
/// When unset, documents the skip — never blocks the user from opening the installer.
/// </summary>
public static class PreUpdateSnapshotGuard
{
    public const string SyncFolderUnsetMessage =
        "OneDrive snapshot folder is unset; skipped pre-update DB snapshot. " +
        "Configure Settings → OneDrive snapshot folder before installing updates if you want an automatic safety copy.";

    public static async Task<PreUpdateSnapshotResult> EnsureAsync(
        string? syncFolder,
        Func<CancellationToken, Task<string?>> createSnapshotAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createSnapshotAsync);

        if (string.IsNullOrWhiteSpace(syncFolder))
        {
            return new PreUpdateSnapshotResult
            {
                Attempted = false,
                SkippedBecauseUnset = true,
                Succeeded = true,
                Message = SyncFolderUnsetMessage,
            };
        }

        try
        {
            var message = await createSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var ok = message is null
                     || (!message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                         && !message.Contains("error", StringComparison.OrdinalIgnoreCase));
            return new PreUpdateSnapshotResult
            {
                Attempted = true,
                SkippedBecauseUnset = false,
                Succeeded = ok,
                Message = string.IsNullOrWhiteSpace(message)
                    ? "Pre-update snapshot completed."
                    : message,
            };
        }
        catch (Exception ex)
        {
            return new PreUpdateSnapshotResult
            {
                Attempted = true,
                SkippedBecauseUnset = false,
                Succeeded = false,
                Message = "Pre-update snapshot failed: " + ex.Message,
            };
        }
    }
}
