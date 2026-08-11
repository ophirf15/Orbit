namespace Orbit.Infrastructure.Files;

public static class FolderAvailability
{
    public const string Available = "available";
    public const string Missing = "missing";
    public const string OfflinePlaceholder = "offline_placeholder";
}

public sealed class ProjectFolderRecord
{
    public required string Id { get; init; }

    public required string ProjectId { get; init; }

    public required string RootPath { get; init; }

    public required string Availability { get; init; }

    public string? LastIndexedAt { get; init; }

    public bool IsHome { get; init; }
}

public sealed class ExternalFileStat
{
    public required string FullPath { get; init; }

    public required string FileName { get; init; }

    public required string Extension { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset ModifiedAtUtc { get; init; }

    public bool IsDirectory { get; init; }

    public string Availability { get; init; } = FolderAvailability.Available;
}

public sealed class IndexedFileRecord
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public string? Extension { get; init; }

    public long? SizeBytes { get; init; }

    public string? ModifiedAt { get; init; }

    public string? ContentHash { get; init; }

    public string? MimeType { get; init; }

    public string? ProjectFolderId { get; init; }

    public string? Availability { get; init; }

    public string? IndexedTextPreview { get; init; }
}

public sealed class FileSearchHit
{
    public required string Id { get; init; }

    public required string Path { get; init; }

    public required string DisplayName { get; init; }

    public string? Extension { get; init; }

    public string? Snippet { get; init; }

    public string? ProjectId { get; init; }
}

public sealed class FileReindexOptions
{
    /// <summary>
    /// When true (default), OneDrive/online-only placeholders are kept in the index as
    /// <see cref="FolderAvailability.OfflinePlaceholder"/> without reading content.
    /// When false, those files are skipped (and pruned if previously indexed).
    /// </summary>
    public bool IncludeOfflinePlaceholders { get; init; } = true;
}

public sealed class FileReindexResult
{
    public int TouchedCount { get; set; }

    public int SkippedUnchangedCount { get; set; }

    public int ExtractedCount { get; set; }

    public int OfflinePlaceholderCount { get; set; }

    public int SoftSkippedDirectoryCount { get; set; }

    public List<string> SoftSkippedDirectories { get; } = new();

    public List<string> SampleRelativePaths { get; } = new();

    /// <summary>Human-readable warning when soft-skips or placeholders hid content.</summary>
    public string? Warning { get; set; }

    public void AddSampleRelativePath(string relativePath, int maxSamples = 8)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || SampleRelativePaths.Count >= maxSamples)
        {
            return;
        }

        if (SampleRelativePaths.Exists(p => string.Equals(p, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        SampleRelativePaths.Add(relativePath);
    }

    public void AddSoftSkippedDirectory(string directoryPath, int maxSamples = 6)
    {
        SoftSkippedDirectoryCount++;
        if (SoftSkippedDirectories.Count >= maxSamples)
        {
            return;
        }

        SoftSkippedDirectories.Add(directoryPath);
    }

    public void FinalizeWarning()
    {
        if (SoftSkippedDirectoryCount <= 0 && OfflinePlaceholderCount <= 0)
        {
            Warning = null;
            return;
        }

        var parts = new List<string>();
        if (SoftSkippedDirectoryCount > 0)
        {
            parts.Add(
                SoftSkippedDirectoryCount == 1
                    ? "Skipped 1 subdirectory (permission denied or cloud placeholder tree)."
                    : $"Skipped {SoftSkippedDirectoryCount} subdirectories (permission denied or cloud placeholder trees).");
            parts.Add("Files under those trees were not indexed.");
        }

        if (OfflinePlaceholderCount > 0)
        {
            parts.Add(
                OfflinePlaceholderCount == 1
                    ? "1 file is an online-only cloud placeholder (metadata only)."
                    : $"{OfflinePlaceholderCount} files are online-only cloud placeholders (metadata only).");
            parts.Add("Make files available offline in OneDrive to extract text.");
        }

        Warning = string.Join(' ', parts);
    }
}
