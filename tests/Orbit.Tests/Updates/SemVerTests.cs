using Orbit.Core.Updates;

namespace Orbit.Tests.Updates;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v0.1.0", 0, 1, 0, null)]
    [InlineData("0.1.0-phase17", 0, 1, 0, "phase17")]
    [InlineData("1.0.0+build.5", 1, 0, 0, null)]
    public void TryParse_AcceptsCommonForms(string text, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.PreRelease);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("v1.0.0", "0.9.9", true)]
    [InlineData("0.1.0", "0.1.0-phase17", true)]
    [InlineData("0.1.0-phase17", "0.1.0", false)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.0.9", "0.1.0", false)]
    public void IsNewer_ComparesCoreAndPrerelease(string candidate, string current, bool expected) =>
        Assert.Equal(expected, SemVer.IsNewer(candidate, current));
}
