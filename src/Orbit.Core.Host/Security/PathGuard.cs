using Orbit.Core.Host;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Files;

namespace Orbit.Core.Host.Security;

public sealed class PathGuard
{
    private readonly HostOptions _options;
    private readonly ProjectFolderStore? _folders;

    public PathGuard(HostOptions options, ProjectFolderStore? folders = null)
    {
        _options = options;
        _folders = folders;
    }

    public string GeneratedFilesRoot => _options.GeneratedFilesRoot;

    public bool TryResolveWritable(string relativeOrAbsolutePath, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
        {
            error = "Path is required.";
            return false;
        }

        try
        {
            fullPath = Path.IsPathRooted(relativeOrAbsolutePath)
                ? PathSafety.NormalizeFullPath(relativeOrAbsolutePath)
                : PathSafety.NormalizeFullPath(Path.Combine(_options.GeneratedFilesRoot, relativeOrAbsolutePath));

            var sandboxes = _folders?.ListActiveHomeSandboxRoots() ?? [];
            PathSafety.EnsureWritablePath(fullPath, _options.GeneratedFilesRoot, sandboxes);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            fullPath = string.Empty;
            return false;
        }
    }

    public bool TryResolveReadable(string path, out string fullPath, out string? error)
    {
        fullPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required.";
            return false;
        }

        try
        {
            fullPath = PathSafety.NormalizeFullPath(path);
            if (PathSafety.IsUnderRoot(fullPath, _options.GeneratedFilesRoot))
            {
                return true;
            }

            var candidate = fullPath;
            var roots = _folders?.ListActiveRootPaths() ?? [];
            if (roots.Any(root => PathSafety.IsUnderRoot(candidate, root)))
            {
                return true;
            }

            error = "Path is outside attached project folders and generated root.";
            fullPath = string.Empty;
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = ex.Message;
            return false;
        }
    }
}
