namespace Orbit.Core.Sync;

/// <summary>Manifest written beside each cloud snapshot DB.</summary>
public sealed class SnapshotManifest
{
    public required string SnapshotId { get; init; }

    public required string SchemaVersion { get; init; }

    public required long Revision { get; init; }

    public required long ParentRevision { get; init; }

    public required string DeviceId { get; init; }

    public required string DeviceName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>SHA-256 hex of orbit.db bytes in the snapshot folder.</summary>
    public required string ChecksumSha256 { get; init; }
}

public enum SyncConflictKind
{
    None = 0,
    DivergedLineage = 1,
    ChecksumMismatch = 2,
    CorruptSnapshot = 3,
}

public enum SyncStatusKind
{
    /// <summary>Sync folder unset or unreachable; capture must still work.</summary>
    Unavailable = 0,
    InSync = 1,
    LocalAhead = 2,
    CloudAhead = 3,
    RestoredFromCloud = 4,
    Conflict = 5,
    Idle = 6,
}

public sealed class SyncConflictInfo
{
    public SyncConflictKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;

    public long? LocalRevision { get; init; }

    public long? CloudRevision { get; init; }

    public string? CloudSnapshotId { get; init; }
}

public sealed class SyncStatus
{
    public SyncStatusKind Kind { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? SyncFolder { get; init; }

    public long LocalRevision { get; init; }

    public long? LatestCloudRevision { get; init; }

    public string? LatestCloudSnapshotId { get; init; }

    public bool LocalDirty { get; init; }

    public SyncConflictInfo? Conflict { get; init; }

    public DateTimeOffset? LastSnapshotAt { get; init; }
}

/// <summary>Local lineage metadata (lives next to the live DB, not in OneDrive).</summary>
public sealed class SyncLineageState
{
    public long Revision { get; set; }

    public long ParentRevision { get; set; }

    public long LastPublishedRevision { get; set; }

    public string? LastPublishedSnapshotId { get; set; }

    public string? LastPublishedChecksumSha256 { get; set; }

    public long LastKnownCloudRevision { get; set; }

    public string? LastKnownCloudSnapshotId { get; set; }

    public bool Dirty { get; set; }

    public SyncConflictInfo? Conflict { get; set; }

    public DateTimeOffset? LastSnapshotAt { get; set; }
}

public sealed class SnapshotSyncOptions
{
    /// <summary>Quiet period after activity before an automatic snapshot.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How often the hosted service polls for quiet-period expiry.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int RetentionCount { get; set; } = 20;
}
