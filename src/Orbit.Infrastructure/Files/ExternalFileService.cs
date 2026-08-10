using System.Diagnostics;
using Orbit.Core.Host;

namespace Orbit.Infrastructure.Files;

public sealed class ExternalFileService : IExternalFileCapability
{
    private readonly Func<IReadOnlyList<string>> _allowedRoots;

    public ExternalFileService(Func<IReadOnlyList<string>> allowedRoots) =>
        _allowedRoots = allowedRoots;

    public IReadOnlyList<ExternalFileStat> List(string rootPath, string? relativeDirectory = null)
    {
        var root = EnsureAllowedRoot(rootPath);
        var target = string.IsNullOrWhiteSpace(relativeDirectory)
            ? root
            : PathSafety.NormalizeFullPath(Path.Combine(root, relativeDirectory));

        if (!PathSafety.IsUnderRoot(target, root))
        {
            throw new UnauthorizedAccessException("List path escapes folder root.");
        }

        if (!Directory.Exists(target))
        {
            return [];
        }

        var results = new List<ExternalFileStat>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(target))
            {
                var stat = TryStat(path);
                if (stat is not null)
                {
                    results.Add(stat);
                }
            }
        }
        catch (Exception ex) when (IsCloudOrIoSoftFailure(ex))
        {
            return results;
        }

        return results;
    }

    public ExternalFileStat? Stat(string fullPath)
    {
        var path = EnsureAllowedPath(fullPath);
        return TryStat(path);
    }

    public Stream OpenRead(string fullPath)
    {
        var path = EnsureAllowedPath(fullPath);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception ex) when (IsCloudOrIoSoftFailure(ex))
        {
            throw new IOException("File is offline or a cloud placeholder.", ex);
        }
    }

    public string? ReadTextPreview(string fullPath, int maxChars = 65_536)
    {
        var path = EnsureAllowedPath(fullPath);
        try
        {
            using var stream = OpenRead(path);
            using var reader = new StreamReader(stream);
            var buffer = new char[Math.Min(maxChars, 65_536)];
            var read = reader.Read(buffer, 0, buffer.Length);
            return read <= 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception ex) when (IsCloudOrIoSoftFailure(ex))
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void OpenExternally(string fullPath)
    {
        var path = EnsureAllowedPath(fullPath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new FileNotFoundException("Path not found.", path);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    private string EnsureAllowedRoot(string rootPath)
    {
        var full = PathSafety.NormalizeFullPath(rootPath);
        var roots = _allowedRoots().Select(PathSafety.NormalizeFullPath).ToList();
        if (roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
        {
            return full;
        }

        throw new UnauthorizedAccessException("Folder root is not an attached project folder.");
    }

    private string EnsureAllowedPath(string path)
    {
        var full = PathSafety.NormalizeFullPath(path);
        if (!_allowedRoots().Any(r => PathSafety.IsUnderRoot(full, r)))
        {
            throw new UnauthorizedAccessException("Path is outside attached project folders.");
        }

        return full;
    }

    private static ExternalFileStat? TryStat(string path)
    {
        try
        {
            var full = PathSafety.NormalizeFullPath(path);
            if (Directory.Exists(full))
            {
                var dir = new DirectoryInfo(full);
                return new ExternalFileStat
                {
                    FullPath = full,
                    FileName = dir.Name,
                    Extension = string.Empty,
                    SizeBytes = 0,
                    ModifiedAtUtc = dir.LastWriteTimeUtc,
                    IsDirectory = true,
                };
            }

            if (!File.Exists(full))
            {
                return null;
            }

            var info = new FileInfo(full);
            return new ExternalFileStat
            {
                FullPath = full,
                FileName = info.Name,
                Extension = info.Extension.TrimStart('.').ToLowerInvariant(),
                SizeBytes = info.Length,
                ModifiedAtUtc = info.LastWriteTimeUtc,
                IsDirectory = false,
                Availability = FolderAvailability.Available,
            };
        }
        catch (Exception ex) when (IsCloudOrIoSoftFailure(ex))
        {
            return new ExternalFileStat
            {
                FullPath = path,
                FileName = Path.GetFileName(path),
                Extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                SizeBytes = 0,
                ModifiedAtUtc = DateTimeOffset.UtcNow,
                IsDirectory = false,
                Availability = FolderAvailability.OfflinePlaceholder,
            };
        }
    }

    internal static bool IsCloudOrIoSoftFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException
        || (ex is AggregateException agg && agg.InnerExceptions.Any(IsCloudOrIoSoftFailure));
}
