using System.Diagnostics;

namespace Orbit.Infrastructure.Hermes;

/// <summary>
/// Ensures a runnable Orbit.Mcp stdio binary exists for Hermes mcp_servers.orbit.
/// Packaged installs ship <c>{app}\orbit-mcp\</c>; Connect syncs that into LocalAppData
/// so Hermes always launches a stable path without needing the Orbit source tree.
/// </summary>
public static class OrbitMcpPublisher
{
    /// <summary>
    /// LocalAppData launch folder Hermes points at. Override with <c>ORBIT_MCP_DIR</c> in tests.
    /// </summary>
    public static string DefaultPublishDirectory
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("ORBIT_MCP_DIR");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.Trim();
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Orbit",
                "orbit-mcp");
        }
    }

    public static string DefaultDllPath => Path.Combine(DefaultPublishDirectory, "Orbit.Mcp.dll");

    public static string DefaultExePath => Path.Combine(DefaultPublishDirectory, "Orbit.Mcp.exe");

    /// <summary>
    /// Returns a path Hermes can launch. Prefers self-contained <c>Orbit.Mcp.exe</c>
    /// (no <c>dotnet</c> on PATH required); falls back to <c>dotnet Orbit.Mcp.dll</c>.
    /// </summary>
    public static string EnsurePublished(string? preferredSource = null)
    {
        // Packaged Orbit ships orbit-mcp beside the app — sync into LocalAppData first.
        SyncBundledIntoLocalAppData();

        if (!string.IsNullOrWhiteSpace(preferredSource) && File.Exists(preferredSource))
        {
            return PreferLaunchable(PublishFrom(preferredSource.Trim()));
        }

        var local = PreferLaunchableIfPresent(DefaultPublishDirectory);
        if (local is not null)
        {
            return local;
        }

        // Dev / CI: copy an already-built output (fast) before a full publish.
        var built = FindBuiltMcp();
        if (built is not null)
        {
            return PreferLaunchable(PublishFrom(built));
        }

        var csproj = FindMcpCsproj();
        if (csproj is not null && TryDotnetPublish(csproj, DefaultPublishDirectory))
        {
            var published = PreferLaunchableIfPresent(DefaultPublishDirectory);
            if (published is not null)
            {
                return published;
            }
        }

        // Last resort: still return the expected LocalAppData path so Connect writes MCP YAML;
        // Hermes will fail loudly until an installer/sync provides the binary.
        return File.Exists(DefaultExePath) ? DefaultExePath : DefaultDllPath;
    }

    /// <summary>
    /// Copies <c>{app}\orbit-mcp</c> (or an explicit source folder) into LocalAppData.
    /// Safe to call repeatedly; overwrites files so upgrades refresh Hermes' launch target.
    /// </summary>
    public static bool SyncBundledIntoLocalAppData(string? bundledDirectory = null)
    {
        var sourceDir = bundledDirectory;
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            sourceDir = FindBundledOrbitMcpDirectory();
        }

        if (sourceDir is null)
        {
            return false;
        }

        var hasPayload = File.Exists(Path.Combine(sourceDir, "Orbit.Mcp.exe"))
            || File.Exists(Path.Combine(sourceDir, "Orbit.Mcp.dll"));
        if (!hasPayload)
        {
            return false;
        }

        // Skip no-op copy when already synced to the same folder.
        if (PathsEqual(sourceDir, DefaultPublishDirectory))
        {
            return true;
        }

        Directory.CreateDirectory(DefaultPublishDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(DefaultPublishDirectory, name), overwrite: true);
        }

        return File.Exists(DefaultExePath) || File.Exists(DefaultDllPath);
    }

    public static string PublishFrom(string sourcePath)
    {
        Directory.CreateDirectory(DefaultPublishDirectory);
        var sourceDir = Path.GetDirectoryName(sourcePath) ?? DefaultPublishDirectory;
        var isExe = sourcePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        // Copy the whole output folder so deps (runtimeconfig, deps.json, nuget dlls) come along.
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(DefaultPublishDirectory, name), overwrite: true);
        }

        return isExe ? DefaultExePath : DefaultDllPath;
    }

    /// <summary>Self-contained exe first (installer path); framework-dependent dll second.</summary>
    public static string PreferLaunchable(string publishedPath)
    {
        if (File.Exists(DefaultExePath))
        {
            return DefaultExePath;
        }

        if (File.Exists(DefaultDllPath))
        {
            return DefaultDllPath;
        }

        return publishedPath;
    }

    private static string? PreferLaunchableIfPresent(string directory)
    {
        var exe = Path.Combine(directory, "Orbit.Mcp.exe");
        if (File.Exists(exe))
        {
            return exe;
        }

        var dll = Path.Combine(directory, "Orbit.Mcp.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static string? FindBundledOrbitMcpDirectory()
    {
        foreach (var start in EnumerateSearchRoots())
        {
            var dir = start;
            for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "orbit-mcp");
                if (Directory.Exists(candidate)
                    && (File.Exists(Path.Combine(candidate, "Orbit.Mcp.exe"))
                        || File.Exists(Path.Combine(candidate, "Orbit.Mcp.dll"))))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string? FindBuiltMcp()
    {
        foreach (var start in EnumerateSearchRoots())
        {
            var dir = start;
            for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                var candidates = new[]
                {
                    Path.Combine(dir.FullName, "src", "Orbit.Mcp", "bin", "Debug", "net9.0", "Orbit.Mcp.dll"),
                    Path.Combine(dir.FullName, "src", "Orbit.Mcp", "bin", "Release", "net9.0", "Orbit.Mcp.dll"),
                    Path.Combine(dir.FullName, "artifacts", "orbit-mcp", "Orbit.Mcp.exe"),
                    Path.Combine(dir.FullName, "artifacts", "orbit-mcp", "Orbit.Mcp.dll"),
                    Path.Combine(dir.FullName, "artifacts", "installer", "publish", "orbit-mcp", "Orbit.Mcp.exe"),
                    Path.Combine(dir.FullName, "artifacts", "installer", "publish", "orbit-mcp", "Orbit.Mcp.dll"),
                };
                foreach (var c in candidates)
                {
                    if (File.Exists(c))
                    {
                        return c;
                    }
                }
            }
        }

        return null;
    }

    private static string? FindMcpCsproj()
    {
        foreach (var start in EnumerateSearchRoots())
        {
            var dir = start;
            for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                var csproj = Path.Combine(dir.FullName, "src", "Orbit.Mcp", "Orbit.Mcp.csproj");
                if (File.Exists(csproj))
                {
                    return csproj;
                }
            }
        }

        return null;
    }

    private static IEnumerable<DirectoryInfo> EnumerateSearchRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path);
                if (seen.Add(full))
                {
                    // deferred yield via list below
                }
            }
            catch
            {
                // ignore bad paths
            }
        }

        var paths = new List<string>();
        try
        {
            paths.Add(AppContext.BaseDirectory);
        }
        catch
        {
            // ignore
        }

        try
        {
            paths.Add(Directory.GetCurrentDirectory());
        }
        catch
        {
            // ignore
        }

        foreach (var p in paths)
        {
            Add(p);
        }

        foreach (var p in seen)
        {
            yield return new DirectoryInfo(p);
        }
    }

    private static bool TryDotnetPublish(string csproj, string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                // Self-contained so Hermes can exec Orbit.Mcp.exe without requiring `dotnet` on PATH.
                Arguments =
                    $"publish \"{csproj}\" -c Release -r win-x64 --self-contained true -o \"{outputDir}\" --nologo -v q",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            _ = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            return proc.WaitForExit(180_000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
