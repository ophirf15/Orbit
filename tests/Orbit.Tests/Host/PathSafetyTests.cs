using Orbit.Core.Host;

namespace Orbit.Tests.Host;

public sealed class PathSafetyTests
{
    [Fact]
    public void IsWritableGeneratedPath_AllowsChildUnderGeneratedRoot()
    {
        using var temp = new TempRoot();
        var generated = Path.Combine(temp.Root, "generated");
        Directory.CreateDirectory(generated);
        var target = Path.Combine(generated, "artifacts", "note.txt");

        Assert.True(PathSafety.IsWritableGeneratedPath(target, generated));
    }

    [Fact]
    public void IsWritableGeneratedPath_DeniesExternalAbsolutePath()
    {
        using var temp = new TempRoot();
        var generated = Path.Combine(temp.Root, "generated");
        Directory.CreateDirectory(generated);
        var external = Path.Combine(temp.Root, "elsewhere", "secret.txt");

        Assert.False(PathSafety.IsWritableGeneratedPath(external, generated));
    }

    [Fact]
    public void IsWritableGeneratedPath_DeniesTraversalEscape()
    {
        using var temp = new TempRoot();
        var generated = Path.Combine(temp.Root, "generated");
        Directory.CreateDirectory(generated);
        var escape = Path.Combine(generated, "..", "outside.txt");

        Assert.False(PathSafety.IsWritableGeneratedPath(escape, generated));
    }

    [Fact]
    public void IsWritableGeneratedPath_DeniesGeneratedRootItself()
    {
        using var temp = new TempRoot();
        var generated = Path.Combine(temp.Root, "generated");
        Directory.CreateDirectory(generated);

        Assert.False(PathSafety.IsWritableGeneratedPath(generated, generated));
    }

    [Fact]
    public void EnsureWritableGeneratedPath_ThrowsForDeniedPath()
    {
        using var temp = new TempRoot();
        var generated = Path.Combine(temp.Root, "generated");
        Directory.CreateDirectory(generated);

        Assert.Throws<UnauthorizedAccessException>(() =>
            PathSafety.EnsureWritableGeneratedPath(Path.Combine(temp.Root, "nope.txt"), generated));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitPathSafety", Guid.NewGuid().ToString("N"));

        public TempRoot() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
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
