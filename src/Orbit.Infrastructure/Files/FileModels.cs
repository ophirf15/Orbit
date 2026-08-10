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

public sealed class FileReindexResult
{
    public int TouchedCount { get; set; }

    public int SkippedUnchangedCount { get; set; }

    public int ExtractedCount { get; set; }
}
