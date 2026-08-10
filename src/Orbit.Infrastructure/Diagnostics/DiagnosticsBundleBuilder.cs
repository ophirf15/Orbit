using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Orbit.Agent.Contracts.Capabilities;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Sync;

namespace Orbit.Infrastructure.Diagnostics;

/// <summary>
/// Builds a redacted diagnostics report (no API keys, hermes key file contents, or email bodies).
/// </summary>
public sealed class DiagnosticsBundleBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SnapshotService _sync;
    private readonly CalendarReadStore _calendar;
    private readonly HermesHealthStatusStore _hermesHealth;
    private readonly string _localDataRoot;
    private readonly string _generatedFilesRoot;

    public DiagnosticsBundleBuilder(
        SqliteConnectionFactory factory,
        SnapshotService sync,
        CalendarReadStore calendar,
        HermesHealthStatusStore hermesHealth,
        string localDataRoot,
        string generatedFilesRoot)
    {
        _factory = factory;
        _sync = sync;
        _calendar = calendar;
        _hermesHealth = hermesHealth;
        _localDataRoot = localDataRoot;
        _generatedFilesRoot = generatedFilesRoot;
    }

    public DiagnosticsReport Build()
    {
        var schemaVersions = SafeGetSchemaVersions();
        var syncStatus = _sync.GetStatus();
        var hermes = _hermesHealth.Read(_localDataRoot);
        var calendar = SafeListCalendar();

        return new DiagnosticsReport
        {
            ExportedAtUtc = DateTime.UtcNow.ToString("O"),
            SchemaVersion = schemaVersions.Count == 0 ? "none" : schemaVersions[^1],
            SchemaVersionsApplied = schemaVersions,
            SyncStatus = ToSyncSummary(syncStatus),
            IndexCounts = CountIndexes(),
            HermesHealthLastKnown = hermes is null
                ? null
                : new HermesHealthSummary
                {
                    Ok = hermes.Ok,
                    StatusCode = hermes.StatusCode,
                    Summary = hermes.Summary,
                    CheckedAtUtc = hermes.CheckedAtUtc,
                },
            CalendarProviders = calendar,
            Capabilities = CapabilityCatalog.All
                .Select(c => new CapabilitySummary
                {
                    Id = c.Id,
                    Route = c.Route,
                    Status = c.Status,
                })
                .ToList(),
            Redactions = ["apiKeys", "hermesKeyFileContents", "emailBodies", "coreHostApiKey"],
        };
    }

    public string WriteJsonExport()
    {
        var report = Build();
        var dir = EnsureExportDir();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
        var path = Path.Combine(dir, $"orbit-diagnostics-{stamp}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        return path;
    }

    public string WriteZipExport()
    {
        var report = Build();
        var dir = EnsureExportDir();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
        var zipPath = Path.Combine(dir, $"orbit-diagnostics-{stamp}.zip");
        var jsonName = "diagnostics.json";

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(jsonName, CompressionLevel.Optimal);
            using var stream = entry.Open();
            JsonSerializer.Serialize(stream, report, JsonOptions);
        }

        return zipPath;
    }

    private string EnsureExportDir()
    {
        var dir = Path.Combine(_generatedFilesRoot, "diagnostics");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private IReadOnlyList<string> SafeGetSchemaVersions()
    {
        try
        {
            return new SqliteMigrator(_factory).GetAppliedVersions();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyList<CalendarProviderSummary> SafeListCalendar()
    {
        try
        {
            return _calendar.ListSources()
                .Select(s => new CalendarProviderSummary
                {
                    Id = s.Id,
                    Name = s.Name,
                    Provider = s.Provider,
                    MailboxName = s.MailboxName,
                    CalendarName = s.CalendarName,
                    Enabled = s.Enabled,
                    LastSyncAt = s.LastSyncAt,
                    LastSyncStatus = s.LastSyncStatus,
                    // lastSyncError is operational metadata, not email body content.
                    LastSyncError = Truncate(s.LastSyncError, 200),
                })
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IndexCountsSummary CountIndexes()
    {
        using var connection = _factory.CreateConnection();
        return new IndexCountsSummary
        {
            FileArtifacts = Count(connection, "file_artifacts"),
            SearchDocuments = Count(connection, "search_documents"),
            EmailArtifacts = Count(connection, "email_artifacts"),
            Projects = Count(connection, "projects"),
            Contacts = Count(connection, "people"),
            CalendarEvents = Count(connection, "calendar_events"),
            AuditEvents = Count(connection, "audit_events"),
        };
    }

    private static long Count(SqliteConnection connection, string table)
    {
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
            var result = cmd.ExecuteScalar();
            return result is long l ? l : Convert.ToInt64(result);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static SyncStatusSummary ToSyncSummary(SyncStatus s) =>
        new()
        {
            Kind = s.Kind.ToString(),
            Message = s.Message,
            LocalRevision = s.LocalRevision,
            LatestCloudRevision = s.LatestCloudRevision,
            LatestCloudSnapshotId = s.LatestCloudSnapshotId,
            LocalDirty = s.LocalDirty,
            LastSnapshotAt = s.LastSnapshotAt?.ToString("O"),
            HasConflict = s.Conflict is not null,
            ConflictKind = s.Conflict?.Kind.ToString(),
            ConflictMessage = s.Conflict?.Message,
        };

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max] + "…";
}

public sealed class DiagnosticsReport
{
    public required string ExportedAtUtc { get; init; }

    public required string SchemaVersion { get; init; }

    public IReadOnlyList<string> SchemaVersionsApplied { get; init; } = [];

    public required SyncStatusSummary SyncStatus { get; init; }

    public required IndexCountsSummary IndexCounts { get; init; }

    public HermesHealthSummary? HermesHealthLastKnown { get; init; }

    public IReadOnlyList<CalendarProviderSummary> CalendarProviders { get; init; } = [];

    public IReadOnlyList<CapabilitySummary> Capabilities { get; init; } = [];

    public IReadOnlyList<string> Redactions { get; init; } = [];
}

public sealed class SyncStatusSummary
{
    public string Kind { get; init; } = string.Empty;

    public string? Message { get; init; }

    public long LocalRevision { get; init; }

    public long? LatestCloudRevision { get; init; }

    public string? LatestCloudSnapshotId { get; init; }

    public bool LocalDirty { get; init; }

    public string? LastSnapshotAt { get; init; }

    public bool HasConflict { get; init; }

    public string? ConflictKind { get; init; }

    public string? ConflictMessage { get; init; }
}

public sealed class IndexCountsSummary
{
    public long FileArtifacts { get; init; }

    public long SearchDocuments { get; init; }

    public long EmailArtifacts { get; init; }

    public long Projects { get; init; }

    public long Contacts { get; init; }

    public long CalendarEvents { get; init; }

    public long AuditEvents { get; init; }
}

public sealed class HermesHealthSummary
{
    public bool Ok { get; init; }

    public int StatusCode { get; init; }

    public string? Summary { get; init; }

    public string? CheckedAtUtc { get; init; }
}

public sealed class CalendarProviderSummary
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Provider { get; init; }

    public string? MailboxName { get; init; }

    public string? CalendarName { get; init; }

    public bool Enabled { get; init; }

    public string? LastSyncAt { get; init; }

    public string? LastSyncStatus { get; init; }

    public string? LastSyncError { get; init; }
}

public sealed class CapabilitySummary
{
    public string Id { get; init; } = string.Empty;

    public string Route { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}
