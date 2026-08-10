using System.Diagnostics;
using System.Text;
using Orbit.Core.Host;

namespace Orbit.Infrastructure.Malleability;

public sealed class DeveloperSourceDeniedException : UnauthorizedAccessException
{
    public DeveloperSourceDeniedException(string message)
        : base(message)
    {
    }
}

public sealed class DeveloperBranchResult
{
    public required string BranchName { get; init; }

    public required string RepoRoot { get; init; }

    public required string Message { get; init; }

    public string? GitOutput { get; init; }
}

public sealed class DeveloperWriteResult
{
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }

    public required string Message { get; init; }
}

public sealed class DeveloperBuildResult
{
    public required bool Success { get; init; }

    public required int ExitCode { get; init; }

    public required string Output { get; init; }

    public required string Message { get; init; }
}

/// <summary>
/// Guarded source-repo operations for Developer Mode. Never writes project folders
/// or installed binaries; paths must resolve under configured SourceRepoRoot.
/// </summary>
public sealed class DeveloperSourceService
{
    public const int DefaultBuildTimeoutMs = 120_000;

    private readonly Func<bool> _developerMode;
    private readonly Func<string?> _sourceRepoRoot;
    private readonly Func<bool> _developerRemoteOverride;
    private readonly Func<IReadOnlyList<string>> _projectFolderRoots;
    private readonly Func<string, string[], string?, int, ProcessRunResult> _runProcess;

    public DeveloperSourceService(
        Func<bool> developerMode,
        Func<string?> sourceRepoRoot,
        Func<bool> developerRemoteOverride,
        Func<IReadOnlyList<string>>? projectFolderRoots = null,
        Func<string, string[], string?, int, ProcessRunResult>? runProcess = null)
    {
        _developerMode = developerMode;
        _sourceRepoRoot = sourceRepoRoot;
        _developerRemoteOverride = developerRemoteOverride;
        _projectFolderRoots = projectFolderRoots ?? (() => Array.Empty<string>());
        _runProcess = runProcess ?? RunProcess;
    }

    public void EnsureDeveloperAccess(string? channel)
    {
        if (!_developerMode())
        {
            throw new DeveloperSourceDeniedException("Developer Mode is off. Enable it in Settings before using source tools.");
        }

        var normalized = channel?.Trim().ToLowerInvariant();
        if (string.Equals(normalized, "telegram", StringComparison.Ordinal)
            && !_developerRemoteOverride())
        {
            throw new DeveloperSourceDeniedException(
                "Developer/source tools are disabled for Telegram sessions unless DeveloperRemoteOverride is enabled.");
        }
    }

    public string ResolveRepoRoot()
    {
        var root = _sourceRepoRoot();
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("SourceRepoRoot is not configured. Set it in Settings (Developer Mode).");
        }

        var full = PathSafety.NormalizeFullPath(root);
        if (!Directory.Exists(full))
        {
            throw new ArgumentException($"SourceRepoRoot does not exist: {full}");
        }

        return full;
    }

    /// <summary>
    /// Ensures a candidate path is under SourceRepoRoot and is not an attached project folder.
    /// </summary>
    public string EnsureWritableUnderRepo(string relativeOrAbsolutePath)
    {
        var repoRoot = ResolveRepoRoot();
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            throw new ArgumentException("Path is required.");
        }

        string full;
        try
        {
            full = Path.IsPathRooted(relativeOrAbsolutePath)
                ? PathSafety.NormalizeFullPath(relativeOrAbsolutePath)
                : PathSafety.NormalizeFullPath(Path.Combine(repoRoot, relativeOrAbsolutePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new DeveloperSourceDeniedException("Invalid path: " + ex.Message);
        }

        if (!PathSafety.IsUnderRoot(full, repoRoot))
        {
            throw new DeveloperSourceDeniedException(
                "Path is outside the configured Orbit source repository. Project folders and arbitrary paths are denied.");
        }

        foreach (var projectRoot in _projectFolderRoots())
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                continue;
            }

            try
            {
                var projectFull = PathSafety.NormalizeFullPath(projectRoot);
                if (PathSafety.IsUnderRoot(full, projectFull)
                    || PathSafety.IsUnderRoot(projectFull, full)
                    || string.Equals(full, projectFull, StringComparison.OrdinalIgnoreCase))
                {
                    // Allow only if the project folder itself is somehow nested under the repo AND the path is still under repo —
                    // still refuse writing *as* a project-folder target when roots overlap by identity of attached folder.
                    if (string.Equals(full, projectFull, StringComparison.OrdinalIgnoreCase)
                        || PathSafety.IsUnderRoot(full, projectFull))
                    {
                        throw new DeveloperSourceDeniedException(
                            "Developer tools must not write attached project folders.");
                    }
                }
            }
            catch (DeveloperSourceDeniedException)
            {
                throw;
            }
            catch (Exception)
            {
                // ignore bad project roots
            }
        }

        return full;
    }

    public DeveloperBranchResult CreateBranch(string branchName, string? channel = null)
    {
        EnsureDeveloperAccess(channel);
        var repoRoot = ResolveRepoRoot();

        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new ArgumentException("branchName is required.");
        }

        var sanitized = branchName.Trim();
        if (sanitized.Length > 120
            || sanitized.Contains("..", StringComparison.Ordinal)
            || sanitized.IndexOfAny([' ', '\t', '\r', '\n', ';', '&', '|', '<', '>']) >= 0)
        {
            throw new ArgumentException("branchName contains invalid characters.");
        }

        var result = _runProcess(
            "git",
            ["checkout", "-b", sanitized],
            repoRoot,
            30_000);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git checkout -b failed (exit {result.ExitCode}): {TrimOutput(result.CombinedOutput)}");
        }

        return new DeveloperBranchResult
        {
            BranchName = sanitized,
            RepoRoot = repoRoot,
            Message = $"Created branch '{sanitized}' in source repo.",
            GitOutput = TrimOutput(result.CombinedOutput),
        };
    }

    public DeveloperWriteResult WriteFileUnderRepo(
        string relativePath,
        string contents,
        string? channel = null)
    {
        EnsureDeveloperAccess(channel);
        var full = EnsureWritableUnderRepo(relativePath);
        var repoRoot = ResolveRepoRoot();

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(full, contents ?? string.Empty, Encoding.UTF8);
        var relative = Path.GetRelativePath(repoRoot, full);
        return new DeveloperWriteResult
        {
            RelativePath = relative,
            FullPath = full,
            Message = "Wrote file under SourceRepoRoot only.",
        };
    }

    public DeveloperBuildResult RunDotnetBuild(string? channel = null, int timeoutMs = DefaultBuildTimeoutMs)
    {
        EnsureDeveloperAccess(channel);
        var repoRoot = ResolveRepoRoot();
        var result = _runProcess(
            "dotnet",
            ["build", "--nologo", "-v", "q"],
            repoRoot,
            timeoutMs);

        return new DeveloperBuildResult
        {
            Success = result.ExitCode == 0,
            ExitCode = result.ExitCode,
            Output = TrimOutput(result.CombinedOutput),
            Message = result.ExitCode == 0
                ? "dotnet build succeeded under SourceRepoRoot."
                : $"dotnet build failed (exit {result.ExitCode}).",
        };
    }

    public sealed record ProcessRunResult(int ExitCode, string CombinedOutput);

    private static ProcessRunResult RunProcess(string fileName, string[] args, string? workingDirectory, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // best-effort
            }

            throw new TimeoutException($"{fileName} timed out after {timeoutMs}ms.");
        }

        var combined = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
        return new ProcessRunResult(process.ExitCode, combined);
    }

    private static string TrimOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        var trimmed = output.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[..4000] + "…";
    }
}
