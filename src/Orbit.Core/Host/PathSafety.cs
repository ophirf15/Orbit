namespace Orbit.Core.Host;

/// <summary>
/// Pure path allow/deny helpers. Project home trees are read-only except the
/// Orbit-owned <c>.orbit</c> sandbox under a home folder (plus global generated root).
/// </summary>
public static class PathSafety
{
    public static string NormalizeFullPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    public static bool IsUnderRoot(string candidatePath, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullCandidate = NormalizeFullPath(candidatePath);
        var fullRoot = NormalizeFullPath(rootPath);

        if (string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the path is strictly under <paramref name="writableRoot"/> (not the root itself).
    /// </summary>
    public static bool IsWritableUnderRoot(string candidatePath, string writableRoot)
        => IsUnderRoot(candidatePath, writableRoot)
           && !string.Equals(
               NormalizeFullPath(candidatePath),
               NormalizeFullPath(writableRoot),
               StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true only when the path resolves strictly under the Orbit-owned generated root.
    /// </summary>
    public static bool IsWritableGeneratedPath(string candidatePath, string generatedFilesRoot)
        => IsWritableUnderRoot(candidatePath, generatedFilesRoot);

    public static void EnsureWritableGeneratedPath(string candidatePath, string generatedFilesRoot)
    {
        if (!IsWritableGeneratedPath(candidatePath, generatedFilesRoot))
        {
            throw new UnauthorizedAccessException(
                "Path is outside Orbit generated-files root or is the root itself.");
        }
    }

    public static void EnsureWritablePath(
        string candidatePath,
        string generatedFilesRoot,
        IEnumerable<string> homeOrbitSandboxRoots)
    {
        if (IsWritableGeneratedPath(candidatePath, generatedFilesRoot))
        {
            return;
        }

        foreach (var sandbox in homeOrbitSandboxRoots)
        {
            if (string.IsNullOrWhiteSpace(sandbox))
            {
                continue;
            }

            if (IsWritableUnderRoot(candidatePath, sandbox))
            {
                return;
            }
        }

        throw new UnauthorizedAccessException(
            "Path is outside Orbit generated-files root and project home .orbit sandboxes.");
    }

    public static bool IsLoopbackAddress(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return false;
        }

        var value = bindAddress.Trim().ToLowerInvariant();
        return value is "127.0.0.1" or "::1" or "localhost";
    }

    /// <summary>
    /// Non-loopback binds require a non-empty API key before the Host may listen.
    /// </summary>
    public static bool CanBind(string bindAddress, string? apiKey)
    {
        if (IsLoopbackAddress(bindAddress))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(apiKey);
    }
}
