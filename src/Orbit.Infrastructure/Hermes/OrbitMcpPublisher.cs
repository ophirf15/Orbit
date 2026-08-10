using System.Diagnostics;

namespace Orbit.Infrastructure.Hermes;

/// <summary>
/// Ensures a runnable Orbit.Mcp stdio binary exists for Hermes mcp_servers.orbit.
/// </summary>
public static class OrbitMcpPublisher
{
    public static string DefaultPublishDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "orbit-mcp");

    public static string DefaultDllPath => Path.Combine(DefaultPublishDirectory, "Orbit.Mcp.dll");

    public static string DefaultExePath => Path.Combine(DefaultPublishDirectory, "Orbit.Mcp.exe");

    /// <summary>
    /// Returns a path Hermes can launch via <c>dotnet &lt;dll&gt;</c>. Prefers a published
    /// LocalAppData copy so Hermes survives Orbit rebuilds.
    /// </summary>
    public static string EnsurePublished(string? preferredSource = null)
    {
        // Prefer a real `dotnet publish` output — copying Debug bin often misses EventLog deps
        // and Hermes then fails with "Connection closed".
        var csproj = FindMcpCsproj();
        if (csproj is not null && TryDotnetPublish(csproj, DefaultPublishDirectory)
            && File.Exists(DefaultDllPath))
        {
            return DefaultDllPath;
        }

        if (!string.IsNullOrWhiteSpace(preferredSource) && File.Exists(preferredSource))
        {
            return PreferDll(PublishFrom(preferredSource.Trim()));
        }

        if (File.Exists(DefaultDllPath))
        {
            return DefaultDllPath;
        }

        var built = FindBuiltMcp();
        if (built is not null)
        {
            return PreferDll(PublishFrom(built));
        }

        return DefaultDllPath;
    }

    private static string PreferDll(string publishedPath)
    {
        // Framework-dependent Orbit.Mcp.exe often fails outside `dotnet` (EventLog binding).
        // Hermes should always launch via: dotnet <Orbit.Mcp.dll>
        if (File.Exists(DefaultDllPath))
        {
            return DefaultDllPath;
        }

        return publishedPath;
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
            // Skip huge/unnecessary artifacts
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(DefaultPublishDirectory, name), overwrite: true);
        }

        return isExe ? DefaultExePath : DefaultDllPath;
    }

    private static string? FindBuiltMcp()
    {
        var roots = new List<DirectoryInfo>();
        try
        {
            roots.Add(new DirectoryInfo(AppContext.BaseDirectory));
        }
        catch
        {
            // ignore
        }

        try
        {
            roots.Add(new DirectoryInfo(Directory.GetCurrentDirectory()));
        }
        catch
        {
            // ignore
        }

        foreach (var start in roots)
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
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var csproj = Path.Combine(dir.FullName, "src", "Orbit.Mcp", "Orbit.Mcp.csproj");
            if (File.Exists(csproj))
            {
                return csproj;
            }
        }

        return null;
    }

    private static bool TryDotnetPublish(string csproj, string outputDir)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish \"{csproj}\" -c Release -o \"{outputDir}\" --nologo -v q",
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
}
