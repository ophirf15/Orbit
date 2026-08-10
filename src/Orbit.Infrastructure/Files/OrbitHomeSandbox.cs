using Orbit.Core.Host;

namespace Orbit.Infrastructure.Files;

/// <summary>
/// Orbit-owned writable island inside a project home folder. Everything else under home is read-only.
/// </summary>
public static class OrbitHomeSandbox
{
    public const string FolderName = ".orbit";

    public static string GetSandboxRoot(string homeRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeRootPath);
        return Path.Combine(PathSafety.NormalizeFullPath(homeRootPath), FolderName);
    }

    public static string EnsureCreated(string homeRootPath)
    {
        var sandbox = GetSandboxRoot(homeRootPath);
        Directory.CreateDirectory(sandbox);

        var readme = Path.Combine(sandbox, "README.txt");
        if (!File.Exists(readme))
        {
            File.WriteAllText(
                readme,
                """
                Orbit sandbox folder
                ====================
                Orbit may create and update files in this .orbit directory only.

                Everything else in the project home folder is read-only to Orbit
                (search, preview, open) — Orbit cannot delete, rename, or overwrite
                your documents outside this folder.
                """);
        }

        return sandbox;
    }
}
