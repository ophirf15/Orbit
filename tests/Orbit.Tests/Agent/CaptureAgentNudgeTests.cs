using Orbit.Core.Agent;

namespace Orbit.Tests.Agent;

public sealed class CaptureAgentNudgeTests
{
    [Fact]
    public void BuildLocal_ReturnsTwoToFourActionableLines()
    {
        var lines = CaptureAgentNudge.BuildLocal("order metrofiber service", "The Harbor Court");
        Assert.InRange(lines.Count, 2, 4);
        Assert.Contains(lines, l => l.Contains("Reword", StringComparison.OrdinalIgnoreCase)
                                    || l.Contains("Captured", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains('?'));
    }

    [Fact]
    public void Format_JoinsLines()
    {
        var text = CaptureAgentNudge.Format(["a", "b"]);
        Assert.Equal("a\nb", text);
    }
}
