using Orbit.Core.Updates;

namespace Orbit.Tests.Updates;

public sealed class PreUpdateSnapshotGuardTests
{
    [Fact]
    public async Task EnsureAsync_SkipsWhenSyncFolderUnset()
    {
        var called = false;
        var result = await PreUpdateSnapshotGuard.EnsureAsync(
            syncFolder: null,
            createSnapshotAsync: _ =>
            {
                called = true;
                return Task.FromResult<string?>("should not run");
            });

        Assert.False(called);
        Assert.True(result.SkippedBecauseUnset);
        Assert.False(result.Attempted);
        Assert.True(result.Succeeded);
        Assert.Contains("unset", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnsureAsync_CallsSnapshotWhenFolderConfigured()
    {
        var called = false;
        var result = await PreUpdateSnapshotGuard.EnsureAsync(
            syncFolder: @"C:\Users\me\OneDrive\OrbitSync",
            createSnapshotAsync: _ =>
            {
                called = true;
                return Task.FromResult<string?>("Created snapshot abc (revision 2).");
            });

        Assert.True(called);
        Assert.True(result.Attempted);
        Assert.True(result.Succeeded);
        Assert.Contains("Created snapshot", result.Message, StringComparison.Ordinal);
    }
}
