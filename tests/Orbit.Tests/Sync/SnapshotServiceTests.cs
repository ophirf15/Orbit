using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Sync;

namespace Orbit.Tests.Sync;

public sealed class SnapshotServiceTests
{
    [Fact]
    public void CreateSnapshot_WritesDbAndManifest_IntoSyncFolder()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "hello snapshot");

        var manifest = env.Service.CreateSnapshot(env.SyncFolder);

        var dir = Path.Combine(env.SyncFolder, SnapshotService.SnapshotsFolderName, manifest.SnapshotId);
        Assert.True(File.Exists(Path.Combine(dir, OrbitDbPaths.DatabaseFileName)));
        Assert.True(File.Exists(Path.Combine(dir, SnapshotService.ManifestFileName)));
        Assert.True(env.Service.VerifyManifest(manifest, dir));
        Assert.Equal(1, manifest.Revision);
        Assert.Equal(0, manifest.ParentRevision);
        Assert.Equal(env.DeviceId, manifest.DeviceId);
    }

    [Fact]
    public void Reconcile_EmptyLocal_OffersContinue_WithoutSilentRestore()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "cloud note");
        var manifest = env.Service.CreateSnapshot(env.SyncFolder);

        // Simulate new machine: wipe live DB + lineage.
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = env.Factory.DatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        File.Delete(env.Lineage.FilePath);
        Directory.CreateDirectory(env.DataRoot);

        var emptyFactory = new SqliteConnectionFactory(env.Factory.DatabasePath);
        var emptyLineage = new SyncLineageStore(env.DataRoot);
        var fresh = new SnapshotService(
            emptyFactory,
            emptyLineage,
            env.DataRoot,
            Guid.NewGuid().ToString("N"),
            "new-machine",
            () => env.SyncFolder);

        var status = fresh.Reconcile(env.SyncFolder);
        Assert.Equal(SyncStatusKind.CloudAhead, status.Kind);
        Assert.True(status.ContinueFromBackupAvailable);
        Assert.Equal(manifest.SnapshotId, status.LatestCloudSnapshotId);
        Assert.False(File.Exists(emptyFactory.DatabasePath));

        // Explicit restore (same path App uses after Continue).
        fresh.RestoreSnapshot(manifest.SnapshotId, env.SyncFolder);
        using var connection = emptyFactory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notes WHERE original_text = $t;";
        cmd.Parameters.AddWithValue("$t", "cloud note");
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
        Assert.Equal(manifest.Revision, emptyLineage.Load().Revision);
    }

    [Fact]
    public void Reconcile_EmptyLocal_AutoRestore_WhenOptedIn()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "cloud note");
        var manifest = env.Service.CreateSnapshot(env.SyncFolder);

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = env.Factory.DatabasePath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        File.Delete(env.Lineage.FilePath);
        Directory.CreateDirectory(env.DataRoot);

        var emptyFactory = new SqliteConnectionFactory(env.Factory.DatabasePath);
        var emptyLineage = new SyncLineageStore(env.DataRoot);
        var fresh = new SnapshotService(
            emptyFactory,
            emptyLineage,
            env.DataRoot,
            Guid.NewGuid().ToString("N"),
            "new-machine",
            () => env.SyncFolder);

        var status = fresh.Reconcile(env.SyncFolder, autoRestoreEmptyLocal: true);
        Assert.Equal(SyncStatusKind.RestoredFromCloud, status.Kind);
        Assert.Equal(manifest.Revision, emptyLineage.Load().Revision);
    }

    [Fact]
    public void TryValidateSyncFolderWritable_AcceptsWritableFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "OrbitSyncWriteProbe", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(SnapshotService.TryValidateSyncFolderWritable(root, out var error), error);
            Assert.Null(error);
            Assert.True(Directory.Exists(Path.Combine(root, SnapshotService.SnapshotsFolderName)));
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    [Fact]
    public void DivergentEdits_ProduceConflict_NotSilentOverwrite()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "base");
        env.Service.CreateSnapshot(env.SyncFolder);

        // Machine A local edit + snapshot
        SeedNote(env.Factory, "local-only");
        env.Lineage.MarkDirty();
        env.Service.NotifyActivity();
        var localSnap = env.Service.CreateSnapshot(env.SyncFolder);

        // Machine B: start from base cloud state in a sibling data root, diverge, publish to same sync folder.
        using var other = new SyncTestEnv(sharedSyncFolder: env.SyncFolder);
        // Restore base by copying first snapshot only — simulate other device from parent revision.
        var snapshots = env.Service.ListSnapshots(env.SyncFolder);
        var baseSnap = snapshots.Last(); // oldest
        other.Service.RestoreSnapshot(baseSnap.SnapshotId, env.SyncFolder);
        SeedNote(other.Factory, "other-only");
        other.Lineage.MarkDirty();
        other.Service.NotifyActivity();
        var otherSnap = other.Service.CreateSnapshot(env.SyncFolder);

        Assert.Equal(localSnap.Revision, otherSnap.Revision);
        Assert.NotEqual(localSnap.SnapshotId, otherSnap.SnapshotId);

        // Local machine still has its own lineage at same revision with different snapshot id.
        var status = env.Service.Reconcile(env.SyncFolder);
        Assert.Equal(SyncStatusKind.Conflict, status.Kind);
        Assert.NotNull(status.Conflict);
        Assert.Equal(SyncConflictKind.DivergedLineage, status.Conflict!.Kind);

        // Live local note must not be wiped by reconcile.
        using var connection = env.Factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notes WHERE original_text = $t;";
        cmd.Parameters.AddWithValue("$t", "local-only");
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Fact]
    public void CorruptChecksum_IsRejected()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "trusted");
        var manifest = env.Service.CreateSnapshot(env.SyncFolder);
        var dir = Path.Combine(env.SyncFolder, SnapshotService.SnapshotsFolderName, manifest.SnapshotId);
        var dbPath = Path.Combine(dir, OrbitDbPaths.DatabaseFileName);
        File.WriteAllBytes(dbPath, [0x00, 0x01, 0x02, 0x03]);

        Assert.False(env.Service.VerifyManifest(manifest, dir));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            env.Service.RestoreSnapshot(manifest.SnapshotId, env.SyncFolder));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RestoreOlderVersion_Succeeds_AndKeepsLastKnownGood()
    {
        using var env = new SyncTestEnv();
        SeedNote(env.Factory, "v1");
        var older = env.Service.CreateSnapshot(env.SyncFolder);
        SeedNote(env.Factory, "v2");
        env.Lineage.MarkDirty();
        env.Service.NotifyActivity();
        var newer = env.Service.CreateSnapshot(env.SyncFolder);
        Assert.True(newer.Revision > older.Revision);

        env.Service.RestoreSnapshot(older.SnapshotId, env.SyncFolder);

        using var connection = env.Factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM notes WHERE original_text = $t;";
        cmd.Parameters.AddWithValue("$t", "v2");
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));

        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT COUNT(*) FROM notes WHERE original_text = $t;";
        cmd.Parameters.AddWithValue("$t", "v1");
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));

        var lkg = Path.Combine(env.DataRoot, SnapshotService.LastKnownGoodFolderName, OrbitDbPaths.DatabaseFileName);
        Assert.True(File.Exists(lkg));
    }

    [Fact]
    public void MissingSyncFolder_DoesNotBlockCapture()
    {
        using var env = new SyncTestEnv();
        env.Service = new SnapshotService(
            env.Factory,
            env.Lineage,
            env.DataRoot,
            env.DeviceId,
            "test-device",
            () => null);

        var notes = new NoteWriteStore(env.Factory, env.Lineage);
        var result = notes.CreateCapture("still works", projectId: null);
        Assert.False(string.IsNullOrWhiteSpace(result.NoteId));

        var status = env.Service.Reconcile();
        Assert.Equal(SyncStatusKind.Unavailable, status.Kind);
    }

    private static void SeedNote(SqliteConnectionFactory factory, string text)
    {
        new SqliteMigrator(factory).ApplyPendingMigrations();
        new NoteWriteStore(factory).CreateCapture(text, projectId: null);
    }

    private sealed class SyncTestEnv : IDisposable
    {
        public string Root { get; }
        public string DataRoot { get; }
        public string SyncFolder { get; }
        public string DeviceId { get; } = Guid.NewGuid().ToString("N");
        public SqliteConnectionFactory Factory { get; }
        public SyncLineageStore Lineage { get; }
        public SnapshotService Service { get; set; }

        public SyncTestEnv(string? sharedSyncFolder = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "OrbitSyncTests", Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(Root, "data");
            SyncFolder = sharedSyncFolder ?? Path.Combine(Root, "onedrive");
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(SyncFolder);
            Factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(DataRoot));
            new SqliteMigrator(Factory).ApplyPendingMigrations();
            Lineage = new SyncLineageStore(DataRoot);
            Service = new SnapshotService(
                Factory,
                Lineage,
                DataRoot,
                DeviceId,
                "test-device",
                () => SyncFolder,
                new SnapshotSyncOptions
                {
                    QuietPeriod = TimeSpan.FromMilliseconds(10),
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    RetentionCount = 10,
                });
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
