using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;

namespace Orbit.Tests.Email;

public sealed class EmailIngestionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "OrbitEmailUnit", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly EmailIngestionService _ingest;

    public EmailIngestionServiceTests()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        var generated = Path.Combine(_root, "generated");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(generated);

        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(data));
        new SqliteMigrator(_factory).ApplyPendingMigrations();

        var store = new EmailArtifactStore(_factory);
        _ingest = new EmailIngestionService(store, new MsgEmailParser(), generated);
    }

    [Fact]
    public void ParseAndIngest_Msg_RetainsMetadataUnderGeneratedRoot()
    {
        var msgPath = MsgFixtureFactory.CopySampleMsg(Path.Combine(_root, "src"));
        var record = _ingest.IngestFromPath(msgPath);

        Assert.False(string.IsNullOrWhiteSpace(record.Id));
        Assert.Equal("Orbit fixture subject", record.Subject);
        Assert.False(string.IsNullOrWhiteSpace(record.ContentHash));
        Assert.False(string.IsNullOrWhiteSpace(record.RawPath));
        Assert.True(File.Exists(record.RawPath));
        Assert.Contains(Path.Combine("generated", "emails"), record.RawPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(record.Participants, p => p.Role == "from" && p.Address.Contains("alice", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(record.Participants, p => p.Role == "to" && p.Address.Contains("bob", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(record.BodyPreview));
        Assert.True(File.Exists(record.BodyTextPath!));
    }

    [Fact]
    public void IngestTwice_SameBytes_DedupsArtifact()
    {
        var msgPath = MsgFixtureFactory.CopySampleMsg(Path.Combine(_root, "src2"));
        var first = _ingest.IngestFromPath(msgPath);
        var second = _ingest.IngestFromPath(msgPath);

        Assert.Equal(first.Id, second.Id);
        Assert.True(second.WasExisting);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
