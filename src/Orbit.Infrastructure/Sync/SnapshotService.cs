using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Sync;

public sealed class SnapshotService
{
    public const string SnapshotsFolderName = "OrbitSnapshots";
    public const string ManifestFileName = "manifest.json";
    public const string LastKnownGoodFolderName = "last-known-good";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SyncLineageStore _lineage;
    private readonly string _localDataRoot;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly Func<string?> _syncFolder;
    private readonly SnapshotSyncOptions _options;
    private readonly object _gate = new();
    private DateTimeOffset _lastActivityUtc = DateTimeOffset.UtcNow;
    private SyncStatus _lastStatus = new()
    {
        Kind = SyncStatusKind.Idle,
        Message = "Sync not evaluated yet.",
    };

    public SnapshotService(
        SqliteConnectionFactory factory,
        SyncLineageStore lineage,
        string localDataRoot,
        string deviceId,
        string deviceName,
        Func<string?> syncFolder,
        SnapshotSyncOptions? options = null)
    {
        _factory = factory;
        _lineage = lineage;
        _localDataRoot = localDataRoot;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? Guid.NewGuid().ToString("N") : deviceId;
        _deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName;
        _syncFolder = syncFolder;
        _options = options ?? new SnapshotSyncOptions();
    }

    public SyncStatus LastStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastStatus;
            }
        }
    }

    public string DeviceId => _deviceId;

    public string DeviceName => _deviceName;

    public TimeSpan QuietPeriod => _options.QuietPeriod;

    /// <summary>
    /// Validates that <paramref name="folder"/> exists (or can be created) and is writable.
    /// Does not place live SQLite in the folder — only a probe file under OrbitSnapshots.
    /// </summary>
    public static bool TryValidateSyncFolderWritable(string? folder, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "Choose a backup folder first.";
            return false;
        }

        try
        {
            var root = folder.Trim();
            Directory.CreateDirectory(root);
            var snapshots = GetSnapshotsRoot(root);
            Directory.CreateDirectory(snapshots);
            var probe = Path.Combine(snapshots, ".orbit-write-probe");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            error = "Folder is not writable: " + ex.Message;
            return false;
        }
    }

    public void NotifyActivity()
    {
        lock (_gate)
        {
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }

        _lineage.MarkDirty();
    }

    public bool ShouldAutoSnapshot(DateTimeOffset utcNow)
    {
        string? folder;
        DateTimeOffset lastActivity;
        lock (_gate)
        {
            lastActivity = _lastActivityUtc;
        }

        folder = TryGetSyncFolder();
        if (folder is null)
        {
            return false;
        }

        var lineage = _lineage.Load();
        if (lineage.Conflict is not null)
        {
            return false;
        }

        var drifted = lineage.Dirty || DetectLocalDrift(lineage);
        if (!drifted && lineage.LastPublishedRevision >= lineage.Revision && lineage.Revision > 0)
        {
            return false;
        }

        return utcNow - lastActivity >= _options.QuietPeriod;
    }

    public SnapshotManifest CreateSnapshot(string? syncFolderOverride = null)
    {
        lock (_gate)
        {
            var syncFolder = ResolveSyncFolder(syncFolderOverride);
            var snapshotsRoot = GetSnapshotsRoot(syncFolder);
            Directory.CreateDirectory(snapshotsRoot);

            var lineage = _lineage.Load();
            var parentRevision = lineage.Revision;
            var revision = parentRevision + 1;
            var snapshotId = $"{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";

            var destDir = Path.Combine(snapshotsRoot, snapshotId);
            Directory.CreateDirectory(destDir);
            var destDb = Path.Combine(destDir, OrbitDbPaths.DatabaseFileName);

            try
            {
                BackupLiveDatabaseTo(destDb);
                var checksum = ComputeSha256Hex(destDb);
                var schemaVersion = ReadLatestSchemaVersion();
                var manifest = new SnapshotManifest
                {
                    SnapshotId = snapshotId,
                    SchemaVersion = schemaVersion,
                    Revision = revision,
                    ParentRevision = parentRevision,
                    DeviceId = _deviceId,
                    DeviceName = _deviceName,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ChecksumSha256 = checksum,
                };

                WriteManifest(destDir, manifest);

                lineage.ParentRevision = parentRevision;
                lineage.Revision = revision;
                lineage.LastPublishedRevision = revision;
                lineage.LastPublishedSnapshotId = snapshotId;
                lineage.LastPublishedChecksumSha256 = checksum;
                lineage.LastKnownCloudRevision = Math.Max(lineage.LastKnownCloudRevision, revision);
                lineage.LastKnownCloudSnapshotId = snapshotId;
                lineage.Dirty = false;
                lineage.Conflict = null;
                lineage.LastSnapshotAt = manifest.CreatedAt;
                _lineage.Save(lineage);

                ApplyRetention(snapshotsRoot);
                _lastActivityUtc = DateTimeOffset.UtcNow;
                _lastStatus = EnrichStatus(new SyncStatus
                {
                    Kind = SyncStatusKind.InSync,
                    Message = $"Snapshot {snapshotId} created (revision {revision}).",
                    SyncFolder = syncFolder,
                    LocalRevision = revision,
                    LatestCloudRevision = revision,
                    LatestCloudSnapshotId = snapshotId,
                    LocalDirty = false,
                    LastSnapshotAt = manifest.CreatedAt,
                });

                return manifest;
            }
            catch
            {
                try
                {
                    if (Directory.Exists(destDir))
                    {
                        Directory.Delete(destDir, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // best-effort cleanup
                }

                throw;
            }
        }
    }

    public IReadOnlyList<SnapshotManifest> ListSnapshots(string? syncFolderOverride = null)
    {
        var syncFolder = TryResolveSyncFolder(syncFolderOverride);
        if (syncFolder is null)
        {
            return [];
        }

        var root = GetSnapshotsRoot(syncFolder);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = new List<SnapshotManifest>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                var manifest = ReadManifest(dir);
                if (manifest is null)
                {
                    continue;
                }

                list.Add(manifest);
            }
            catch (Exception)
            {
                // skip unreadable
            }
        }

        return list
            .OrderByDescending(m => m.Revision)
            .ThenByDescending(m => m.CreatedAt)
            .ToList();
    }

    public bool VerifyManifest(SnapshotManifest manifest, string snapshotDirectory)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);

        var dbPath = Path.Combine(snapshotDirectory, OrbitDbPaths.DatabaseFileName);
        if (!File.Exists(dbPath))
        {
            return false;
        }

        var actual = ComputeSha256Hex(dbPath);
        return string.Equals(actual, manifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase);
    }

    public SnapshotManifest RestoreSnapshot(string snapshotId, string? syncFolderOverride = null, bool allowDuringConflict = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        lock (_gate)
        {
            var syncFolder = ResolveSyncFolder(syncFolderOverride);
            var dir = Path.Combine(GetSnapshotsRoot(syncFolder), snapshotId);
            if (!Directory.Exists(dir))
            {
                throw new FileNotFoundException("Snapshot folder was not found.", dir);
            }

            var manifest = ReadManifest(dir)
                ?? throw new InvalidOperationException("Snapshot manifest.json is missing or invalid.");

            if (!VerifyManifest(manifest, dir))
            {
                var conflict = new SyncConflictInfo
                {
                    Kind = SyncConflictKind.CorruptSnapshot,
                    Message = $"Snapshot {snapshotId} failed checksum verification and was rejected.",
                    CloudSnapshotId = snapshotId,
                    CloudRevision = manifest.Revision,
                    LocalRevision = _lineage.Load().Revision,
                };
                var lineageCorrupt = _lineage.Load();
                lineageCorrupt.Conflict = conflict;
                _lineage.Save(lineageCorrupt);
                _lastStatus = BuildStatus(SyncStatusKind.Conflict, conflict.Message, syncFolder, lineageCorrupt, manifest);
                throw new InvalidOperationException(conflict.Message);
            }

            if (!allowDuringConflict)
            {
                var existing = _lineage.Load();
                if (existing.Conflict is not null)
                {
                    throw new InvalidOperationException(
                        "Sync conflict is active; restore only via explicit user restore.");
                }
            }

            PreserveLastKnownGood();
            var sourceDb = Path.Combine(dir, OrbitDbPaths.DatabaseFileName);
            ReplaceLiveDatabase(sourceDb);

            var lineage = _lineage.Load();
            lineage.Revision = manifest.Revision;
            lineage.ParentRevision = manifest.ParentRevision;
            lineage.LastPublishedRevision = manifest.Revision;
            lineage.LastPublishedSnapshotId = manifest.SnapshotId;
            lineage.LastPublishedChecksumSha256 = manifest.ChecksumSha256;
            lineage.LastKnownCloudRevision = manifest.Revision;
            lineage.LastKnownCloudSnapshotId = manifest.SnapshotId;
            lineage.Dirty = false;
            lineage.Conflict = null;
            lineage.LastSnapshotAt = manifest.CreatedAt;
            _lineage.Save(lineage);

            // Ensure migrations are applied after restore.
            new SqliteMigrator(_factory).ApplyPendingMigrations();

            _lastStatus = EnrichStatus(new SyncStatus
            {
                Kind = SyncStatusKind.RestoredFromCloud,
                Message = $"Restored snapshot {snapshotId} (revision {manifest.Revision}).",
                SyncFolder = syncFolder,
                LocalRevision = manifest.Revision,
                LatestCloudRevision = manifest.Revision,
                LatestCloudSnapshotId = snapshotId,
                LocalDirty = false,
                LastSnapshotAt = manifest.CreatedAt,
            });

            return manifest;
        }
    }

    /// <summary>
    /// Compares local lineage vs cloud snapshots. Empty local + cloud snapshots surfaces
    /// <see cref="SyncStatus.ContinueFromBackupAvailable"/> unless
    /// <paramref name="autoRestoreEmptyLocal"/> is true (tests / explicit opt-in only).
    /// Divergent dirty local never silently overwrites (ADR 0016).
    /// </summary>
    public SyncStatus Reconcile(string? syncFolderOverride = null, bool autoRestoreEmptyLocal = false)
    {
        lock (_gate)
        {
            var syncFolder = TryResolveSyncFolder(syncFolderOverride);
            var lineage = _lineage.Load();

            if (syncFolder is null)
            {
                _lastStatus = EnrichStatus(new SyncStatus
                {
                    Kind = SyncStatusKind.Unavailable,
                    Message = "OneDrive snapshot folder is not configured or unavailable.",
                    SyncFolder = null,
                    LocalRevision = lineage.Revision,
                    LocalDirty = lineage.Dirty,
                    Conflict = lineage.Conflict,
                    LastSnapshotAt = lineage.LastSnapshotAt,
                });
                return _lastStatus;
            }

            IReadOnlyList<SnapshotManifest> snapshots;
            try
            {
                snapshots = ListSnapshots(syncFolder);
            }
            catch (Exception ex)
            {
                _lastStatus = EnrichStatus(new SyncStatus
                {
                    Kind = SyncStatusKind.Unavailable,
                    Message = "Sync folder offline or unreadable: " + ex.Message,
                    SyncFolder = syncFolder,
                    LocalRevision = lineage.Revision,
                    LocalDirty = lineage.Dirty,
                    LastSnapshotAt = lineage.LastSnapshotAt,
                });
                return _lastStatus;
            }

            var latest = snapshots.FirstOrDefault();
            if (latest is null)
            {
                _lastStatus = EnrichStatus(new SyncStatus
                {
                    Kind = lineage.Revision > 0 ? SyncStatusKind.LocalAhead : SyncStatusKind.Idle,
                    Message = lineage.Revision > 0
                        ? "No cloud snapshots yet; local state will publish on next snapshot."
                        : "No cloud snapshots and empty local lineage.",
                    SyncFolder = syncFolder,
                    LocalRevision = lineage.Revision,
                    LocalDirty = lineage.Dirty,
                    LastSnapshotAt = lineage.LastSnapshotAt,
                });
                return _lastStatus;
            }

            lineage.LastKnownCloudRevision = latest.Revision;
            lineage.LastKnownCloudSnapshotId = latest.SnapshotId;

            var effectiveDirty = lineage.Dirty || DetectLocalDrift(lineage);
            lineage.Dirty = effectiveDirty;

            var localEmpty = IsLocalDatabaseEmpty();
            if (localEmpty)
            {
                if (!autoRestoreEmptyLocal)
                {
                    lineage.Conflict = null;
                    _lineage.Save(lineage);
                    _lastStatus = EnrichStatus(new SyncStatus
                    {
                        Kind = SyncStatusKind.CloudAhead,
                        Message =
                            $"Local database is empty; cloud snapshot {latest.SnapshotId} (revision {latest.Revision}) is available to continue.",
                        SyncFolder = syncFolder,
                        LocalRevision = 0,
                        LatestCloudRevision = latest.Revision,
                        LatestCloudSnapshotId = latest.SnapshotId,
                        LocalDirty = false,
                        LastSnapshotAt = latest.CreatedAt,
                        ContinueFromBackupAvailable = true,
                    });
                    return _lastStatus;
                }

                try
                {
                    RestoreSnapshot(latest.SnapshotId, syncFolder, allowDuringConflict: true);
                    return _lastStatus;
                }
                catch (Exception ex)
                {
                    _lastStatus = EnrichStatus(new SyncStatus
                    {
                        Kind = SyncStatusKind.Conflict,
                        Message = "Failed to restore cloud snapshot onto empty local DB: " + ex.Message,
                        SyncFolder = syncFolder,
                        LocalRevision = 0,
                        LatestCloudRevision = latest.Revision,
                        LatestCloudSnapshotId = latest.SnapshotId,
                        Conflict = new SyncConflictInfo
                        {
                            Kind = SyncConflictKind.CorruptSnapshot,
                            Message = ex.Message,
                            CloudRevision = latest.Revision,
                            CloudSnapshotId = latest.SnapshotId,
                            LocalRevision = 0,
                        },
                    });
                    return _lastStatus;
                }
            }

            if (latest.Revision > lineage.Revision)
            {
                var localIsAncestor = IsAncestor(snapshots, descendant: latest, ancestorRevision: lineage.Revision);
                if (!effectiveDirty && localIsAncestor)
                {
                    try
                    {
                        RestoreSnapshot(latest.SnapshotId, syncFolder, allowDuringConflict: true);
                        return _lastStatus;
                    }
                    catch (Exception ex)
                    {
                        var conflict = new SyncConflictInfo
                        {
                            Kind = SyncConflictKind.CorruptSnapshot,
                            Message = ex.Message,
                            LocalRevision = lineage.Revision,
                            CloudRevision = latest.Revision,
                            CloudSnapshotId = latest.SnapshotId,
                        };
                        lineage.Conflict = conflict;
                        _lineage.Save(lineage);
                        _lastStatus = BuildStatus(SyncStatusKind.Conflict, conflict.Message, syncFolder, lineage, latest);
                        return _lastStatus;
                    }
                }

                var conflictDiverged = new SyncConflictInfo
                {
                    Kind = SyncConflictKind.DivergedLineage,
                    Message =
                        $"Local revision {lineage.Revision} and cloud revision {latest.Revision} diverged; auto-merge stopped.",
                    LocalRevision = lineage.Revision,
                    CloudRevision = latest.Revision,
                    CloudSnapshotId = latest.SnapshotId,
                };
                lineage.Conflict = conflictDiverged;
                _lineage.Save(lineage);
                _lastStatus = BuildStatus(SyncStatusKind.Conflict, conflictDiverged.Message, syncFolder, lineage, latest);
                return _lastStatus;
            }

            if (latest.Revision < lineage.Revision)
            {
                lineage.Conflict = null;
                _lineage.Save(lineage);
                _lastStatus = EnrichStatus(new SyncStatus
                {
                    Kind = SyncStatusKind.LocalAhead,
                    Message = $"Local revision {lineage.Revision} is ahead of cloud {latest.Revision}.",
                    SyncFolder = syncFolder,
                    LocalRevision = lineage.Revision,
                    LatestCloudRevision = latest.Revision,
                    LatestCloudSnapshotId = latest.SnapshotId,
                    LocalDirty = lineage.Dirty,
                    LastSnapshotAt = lineage.LastSnapshotAt,
                });
                return _lastStatus;
            }

            // Same revision number — compare identity/checksum.
            var latestDir = Path.Combine(GetSnapshotsRoot(syncFolder), latest.SnapshotId);
            if (!VerifyManifest(latest, latestDir))
            {
                var conflict = new SyncConflictInfo
                {
                    Kind = SyncConflictKind.CorruptSnapshot,
                    Message = $"Latest cloud snapshot {latest.SnapshotId} failed checksum verification.",
                    LocalRevision = lineage.Revision,
                    CloudRevision = latest.Revision,
                    CloudSnapshotId = latest.SnapshotId,
                };
                lineage.Conflict = conflict;
                _lineage.Save(lineage);
                _lastStatus = BuildStatus(SyncStatusKind.Conflict, conflict.Message, syncFolder, lineage, latest);
                return _lastStatus;
            }

            if (!string.Equals(lineage.LastPublishedSnapshotId, latest.SnapshotId, StringComparison.Ordinal)
                && (effectiveDirty
                    || !string.Equals(
                        lineage.LastPublishedChecksumSha256,
                        latest.ChecksumSha256,
                        StringComparison.OrdinalIgnoreCase)))
            {
                var conflict = new SyncConflictInfo
                {
                    Kind = SyncConflictKind.DivergedLineage,
                    Message =
                        $"Both sides advanced from revision {latest.ParentRevision} to {latest.Revision} with different snapshots.",
                    LocalRevision = lineage.Revision,
                    CloudRevision = latest.Revision,
                    CloudSnapshotId = latest.SnapshotId,
                };
                lineage.Conflict = conflict;
                _lineage.Save(lineage);
                _lastStatus = BuildStatus(SyncStatusKind.Conflict, conflict.Message, syncFolder, lineage, latest);
                return _lastStatus;
            }

            lineage.Conflict = null;
            _lineage.Save(lineage);
            _lastStatus = EnrichStatus(new SyncStatus
            {
                Kind = effectiveDirty ? SyncStatusKind.LocalAhead : SyncStatusKind.InSync,
                Message = effectiveDirty
                    ? "Local changes pending snapshot."
                    : "Local and cloud revisions match.",
                SyncFolder = syncFolder,
                LocalRevision = lineage.Revision,
                LatestCloudRevision = latest.Revision,
                LatestCloudSnapshotId = latest.SnapshotId,
                LocalDirty = effectiveDirty,
                LastSnapshotAt = lineage.LastSnapshotAt,
            });
            return _lastStatus;
        }
    }

    public SyncStatus GetStatus()
    {
        bool needsReconcile;
        lock (_gate)
        {
            needsReconcile = _lastStatus.Kind == SyncStatusKind.Idle;
        }

        if (needsReconcile)
        {
            return Reconcile();
        }

        lock (_gate)
        {
            var lineage = _lineage.Load();
            var folder = TryGetSyncFolder();
            var continueOffer = _lastStatus.ContinueFromBackupAvailable
                || (IsLocalDatabaseEmpty()
                    && !string.IsNullOrWhiteSpace(
                        _lastStatus.LatestCloudSnapshotId ?? lineage.LastKnownCloudSnapshotId)
                    && folder is not null);
            return EnrichStatus(new SyncStatus
            {
                Kind = _lastStatus.Kind,
                Message = _lastStatus.Message,
                SyncFolder = folder,
                LocalRevision = lineage.Revision,
                LatestCloudRevision = _lastStatus.LatestCloudRevision ?? lineage.LastKnownCloudRevision,
                LatestCloudSnapshotId = _lastStatus.LatestCloudSnapshotId ?? lineage.LastKnownCloudSnapshotId,
                LocalDirty = lineage.Dirty,
                Conflict = lineage.Conflict ?? _lastStatus.Conflict,
                LastSnapshotAt = lineage.LastSnapshotAt ?? _lastStatus.LastSnapshotAt,
                ContinueFromBackupAvailable = continueOffer,
            });
        }
    }

    private bool DetectLocalDrift(SyncLineageState lineage)
    {
        if (string.IsNullOrWhiteSpace(lineage.LastPublishedChecksumSha256))
        {
            return false;
        }

        if (!File.Exists(_factory.DatabasePath))
        {
            return false;
        }

        var temp = Path.Combine(Path.GetTempPath(), "orbit-drift-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            BackupLiveDatabaseTo(temp);
            var hash = ComputeSha256Hex(temp);
            return !string.Equals(hash, lineage.LastPublishedChecksumSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return lineage.Dirty;
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // ignore
            }
        }
    }

    private SyncStatus EnrichStatus(SyncStatus status)
    {
        var quiet = _options.QuietPeriod;
        var quietLabel = quiet.TotalMinutes >= 1
            ? $"auto-backup after {quiet.TotalMinutes:0.#}m quiet"
            : $"auto-backup after {quiet.TotalSeconds:0}s quiet";
        return new SyncStatus
        {
            Kind = status.Kind,
            Message = status.Message,
            SyncFolder = status.SyncFolder,
            LocalRevision = status.LocalRevision,
            LatestCloudRevision = status.LatestCloudRevision,
            LatestCloudSnapshotId = status.LatestCloudSnapshotId,
            LocalDirty = status.LocalDirty,
            Conflict = status.Conflict,
            LastSnapshotAt = status.LastSnapshotAt,
            DeviceId = _deviceId,
            ContinueFromBackupAvailable = status.ContinueFromBackupAvailable,
            AutoBackupHint = string.IsNullOrWhiteSpace(status.SyncFolder)
                ? "Backup folder not configured."
                : status.Kind == SyncStatusKind.Unavailable
                    ? "Backup unavailable (folder offline)."
                    : status.LocalDirty || status.Kind == SyncStatusKind.LocalAhead
                        ? $"Pending changes · {quietLabel}"
                        : quietLabel,
        };
    }

    private SyncStatus BuildStatus(
        SyncStatusKind kind,
        string message,
        string syncFolder,
        SyncLineageState lineage,
        SnapshotManifest? latest)
    {
        return EnrichStatus(new SyncStatus
        {
            Kind = kind,
            Message = message,
            SyncFolder = syncFolder,
            LocalRevision = lineage.Revision,
            LatestCloudRevision = latest?.Revision ?? lineage.LastKnownCloudRevision,
            LatestCloudSnapshotId = latest?.SnapshotId ?? lineage.LastKnownCloudSnapshotId,
            LocalDirty = lineage.Dirty,
            Conflict = lineage.Conflict,
            LastSnapshotAt = lineage.LastSnapshotAt,
        });
    }

    private static bool IsAncestor(
        IReadOnlyList<SnapshotManifest> snapshots,
        SnapshotManifest descendant,
        long ancestorRevision)
    {
        if (ancestorRevision == 0)
        {
            return true;
        }

        if (descendant.Revision == ancestorRevision)
        {
            return true;
        }

        var byRevision = snapshots
            .GroupBy(s => s.Revision)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First());

        var current = descendant;
        var guard = 0;
        while (guard++ < 10_000)
        {
            if (current.ParentRevision == ancestorRevision || current.Revision == ancestorRevision)
            {
                return true;
            }

            if (current.ParentRevision <= 0)
            {
                return ancestorRevision == 0;
            }

            if (!byRevision.TryGetValue(current.ParentRevision, out var parent))
            {
                // Missing history — only trust direct parent match.
                return current.ParentRevision == ancestorRevision;
            }

            current = parent;
        }

        return false;
    }

    private void BackupLiveDatabaseTo(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(_factory.DatabasePath))
        {
            // Ensure empty schema exists so backup has a valid DB.
            using var warm = _factory.CreateConnection();
            new SqliteMigrator(_factory).ApplyPendingMigrations();
        }

        using (var source = _factory.CreateConnection())
        using (var dest = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            dest.Open();
            source.BackupDatabase(dest);
        }

        SqliteConnection.ClearAllPools();
    }

    private void PreserveLastKnownGood()
    {
        if (!File.Exists(_factory.DatabasePath))
        {
            return;
        }

        var lkgDir = Path.Combine(_localDataRoot, LastKnownGoodFolderName);
        Directory.CreateDirectory(lkgDir);
        var lkgDb = Path.Combine(lkgDir, OrbitDbPaths.DatabaseFileName);
        try
        {
            BackupLiveDatabaseTo(lkgDb);
            var lineageCopy = Path.Combine(lkgDir, SyncLineageStore.FileName);
            if (File.Exists(_lineage.FilePath))
            {
                File.Copy(_lineage.FilePath, lineageCopy, overwrite: true);
            }
        }
        catch (Exception)
        {
            // never block restore path hard on LKG failure — still try replace after best-effort
        }
    }

    private void ReplaceLiveDatabase(string sourceDbPath)
    {
        // Checkpoint/close WAL by opening and disposing, then replace files.
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var live = _factory.DatabasePath;
        var dir = Path.GetDirectoryName(live);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = live + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        File.Copy(sourceDbPath, live, overwrite: true);
    }

    private bool IsLocalDatabaseEmpty()
    {
        if (!File.Exists(_factory.DatabasePath))
        {
            return true;
        }

        try
        {
            using var connection = _factory.CreateConnection();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                  (SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%')
                ;
                """;
            var tables = Convert.ToInt64(cmd.ExecuteScalar());
            if (tables == 0)
            {
                return true;
            }

            // Treat freshly migrated empty graph as empty for "new machine" restore.
            using var notes = connection.CreateCommand();
            notes.CommandText =
                """
                SELECT
                  COALESCE((SELECT COUNT(*) FROM projects), 0) +
                  COALESCE((SELECT COUNT(*) FROM notes), 0) +
                  COALESCE((SELECT COUNT(*) FROM tasks), 0);
                """;
            try
            {
                var rows = Convert.ToInt64(notes.ExecuteScalar());
                var lineage = _lineage.Load();
                return rows == 0 && lineage.Revision == 0 && !lineage.Dirty;
            }
            catch (SqliteException)
            {
                return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string ReadLatestSchemaVersion()
    {
        try
        {
            var versions = new SqliteMigrator(_factory).GetAppliedVersions();
            return versions.Count == 0 ? "none" : versions[^1];
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private void ApplyRetention(string snapshotsRoot)
    {
        var keep = Math.Max(1, _options.RetentionCount);
        var dirs = Directory.EnumerateDirectories(snapshotsRoot)
            .Select(d =>
            {
                var m = ReadManifest(d);
                return (Dir: d, Manifest: m);
            })
            .Where(x => x.Manifest is not null)
            .OrderByDescending(x => x.Manifest!.Revision)
            .ThenByDescending(x => x.Manifest!.CreatedAt)
            .ToList();

        foreach (var extra in dirs.Skip(keep))
        {
            try
            {
                Directory.Delete(extra.Dir, recursive: true);
            }
            catch (IOException)
            {
                // ignore retention failures
            }
        }
    }

    private static void WriteManifest(string directory, SnapshotManifest manifest)
    {
        var path = Path.Combine(directory, ManifestFileName);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static SnapshotManifest? ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SnapshotManifest>(json, JsonOptions);
    }

    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string GetSnapshotsRoot(string syncFolder) =>
        Path.Combine(syncFolder, SnapshotsFolderName);

    private string? TryGetSyncFolder() => TryResolveSyncFolder(null);

    private string ResolveSyncFolder(string? syncFolderOverride)
    {
        var folder = TryResolveSyncFolder(syncFolderOverride);
        if (folder is null)
        {
            throw new InvalidOperationException(
                "OneDrive snapshot folder is not configured or is unavailable.");
        }

        return folder;
    }

    private string? TryResolveSyncFolder(string? syncFolderOverride)
    {
        var folder = string.IsNullOrWhiteSpace(syncFolderOverride)
            ? _syncFolder()
            : syncFolderOverride.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
