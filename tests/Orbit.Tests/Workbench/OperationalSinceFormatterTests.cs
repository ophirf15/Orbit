using Orbit.Core.Workbench;

namespace Orbit.Tests.Workbench;

public sealed class OperationalSinceFormatterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FormatSince_NullOrInvalid_ReturnsNull()
    {
        Assert.Null(OperationalSinceFormatter.FormatSince(null, Now));
        Assert.Null(OperationalSinceFormatter.FormatSince("not-a-date", Now));
    }

    [Fact]
    public void FormatSince_HoursAndDays()
    {
        Assert.Equal(
            "Since 3 hours ago",
            OperationalSinceFormatter.FormatSince(Now.AddHours(-3).ToString("o"), Now));
        Assert.Equal(
            "Since 4 days ago",
            OperationalSinceFormatter.FormatSince(Now.AddDays(-4).ToString("o"), Now));
    }
}
