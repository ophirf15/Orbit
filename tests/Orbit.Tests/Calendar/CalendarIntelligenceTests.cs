using Orbit.Core.Data;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;

namespace Orbit.Tests.Calendar;

public sealed class CalendarIntelligenceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "OrbitCalendarTests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public CalendarIntelligenceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(Path.Combine(_root, "data")));
        new SqliteMigrator(_factory).ApplyPendingMigrations();
    }

    [Fact]
    public async Task IcsProvider_ParsesFixtureEvents()
    {
        var path = CopyFixture("harbor-mailbox.ics");
        var provider = new IcsCalendarProvider(path, "Harbor Court mailbox");
        var result = await provider.ReadAsync();

        Assert.True(result.Available);
        Assert.Single(result.Sources);
        Assert.Equal(2, result.Sources[0].Events.Count);
        Assert.Contains(result.Sources[0].Events, e => e.Title.Contains("Harbor Court", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sync_TwoIcsSources_RemainDistinguishable()
    {
        var a = CopyFixture("harbor-mailbox.ics");
        var b = CopyFixture("riverview-mailbox.ics");
        var sync = new CalendarSyncService(
            _factory,
            providersFactory: () =>
            [
                new IcsCalendarProvider(a, "Mailbox A / Calendar"),
                new IcsCalendarProvider(b, "Mailbox B / Calendar"),
                new GraphCalendarProvider(),
            ]);

        var result = await sync.SyncAsync();
        Assert.True(result.SourcesUpserted >= 2);

        var sources = new CalendarReadStore(_factory).ListSources();
        var ics = sources.Where(s => s.Provider == CalendarProviders.Ics).ToList();
        Assert.True(ics.Count >= 2);
        Assert.Contains(ics, s => s.Name.Contains("Mailbox A", StringComparison.OrdinalIgnoreCase)
            || (s.CalendarName?.Contains("Mailbox A", StringComparison.OrdinalIgnoreCase) ?? false)
            || s.Name.Contains("harbor", StringComparison.OrdinalIgnoreCase)
            || (s.ConfigUri?.Contains("harbor", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Contains(ics, s =>
            (s.ConfigUri?.Contains("riverview", StringComparison.OrdinalIgnoreCase) ?? false)
            || s.Name.Contains("Mailbox B", StringComparison.OrdinalIgnoreCase)
            || s.Name.Contains("riverview", StringComparison.OrdinalIgnoreCase));

        var distinctKeys = ics.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(ics.Count, distinctKeys);
    }

    [Fact]
    public async Task HarborCourtMeeting_RaisesAttention_WithoutChangingPriority()
    {
        SeedHarborCourtProject(out var projectId, out var taskId, out var priorityBefore);

        var path = CopyFixture("harbor-mailbox.ics");
        // Force imminence: rewrite fixture times relative to now via subscribe + manual event upsert path
        var sync = new CalendarSyncService(
            _factory,
            providersFactory: () => [new IcsCalendarProvider(path, "Harbor Court ICS")]);
        await sync.SyncAsync();

        // Bump the Harbor Court event start into the imminent window and rescore.
        using (var connection = _factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                UPDATE calendar_events
                SET starts_at = $start, ends_at = $end
                WHERE title LIKE '%Harbor Court%';
                """;
            cmd.Parameters.AddWithValue("$start", DateTime.UtcNow.AddHours(3).ToString("O"));
            cmd.Parameters.AddWithValue("$end", DateTime.UtcNow.AddHours(4).ToString("O"));
            cmd.ExecuteNonQuery();
        }

        new MeetingProjectLinker(_factory).LinkAll();
        new AttentionScorer(_factory, () => DateTimeOffset.UtcNow).RescoreAll();

        double? attention;
        using (var connection = _factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT e.attention_score
                FROM calendar_events e
                INNER JOIN event_entity_links l
                  ON l.calendar_event_id = e.id
                 AND l.entity_type = 'project'
                 AND l.entity_id = $p
                WHERE e.title LIKE '%Harbor Court%'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$p", projectId);
            var scalar = cmd.ExecuteScalar();
            attention = scalar is null or DBNull ? null : Convert.ToDouble(scalar);
        }

        Assert.NotNull(attention);
        Assert.True(attention >= 0.8, $"Expected high attention for imminent Harbor Court meeting, got {attention}");

        int? priorityAfter;
        using (var connection = _factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT priority FROM tasks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", taskId);
            priorityAfter = Convert.ToInt32(cmd.ExecuteScalar());
        }

        Assert.Equal(priorityBefore, priorityAfter);
    }

    [Fact]
    public async Task ProviderInterface_GraphStub_IsUnavailable()
    {
        ICalendarProvider provider = new GraphCalendarProvider();
        Assert.Equal(CalendarProviders.Graph, provider.ProviderId);
        var result = await provider.ReadAsync();
        Assert.False(result.Available);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public void OutlookProvider_ImplementsSwappableInterface()
    {
        ICalendarProvider provider = new OutlookCalendarProvider();
        Assert.Equal(CalendarProviders.Outlook, provider.ProviderId);
        // Live COM discovery is environment-verified (docs/TODO.md); do not call ReadAsync in CI.
    }

    [Fact]
    public async Task ContextBundle_IncludesLinkedMeetings()
    {
        SeedHarborCourtProject(out var projectId, out _, out _);
        var path = CopyFixture("harbor-mailbox.ics");
        var sync = new CalendarSyncService(
            _factory,
            providersFactory: () => [new IcsCalendarProvider(path)]);
        await sync.SyncAsync();

        var bundle = new ContextBundleService(_factory).GetBundle("project", projectId);
        Assert.NotNull(bundle);
        Assert.Contains(bundle!.Meetings, m => m.Title.Contains("Harbor Court", StringComparison.OrdinalIgnoreCase));
    }

    private void SeedHarborCourtProject(out string projectId, out string taskId, out int priority)
    {
        projectId = Guid.NewGuid().ToString("D");
        taskId = Guid.NewGuid().ToString("D");
        priority = 3;
        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();
        Exec(connection, tx,
            """
            INSERT INTO projects (id, name, code, status, created_at, updated_at)
            VALUES ($id, 'Harbor Court', 'COL', 'active', $t, $t);
            """,
            ("$id", projectId), ("$t", now));
        Exec(connection, tx,
            """
            INSERT INTO tasks (id, project_id, title, status, priority, created_at, updated_at)
            VALUES ($id, $p, 'Schedule survey', 'active', $pri, $t, $t);
            """,
            ("$id", taskId), ("$p", projectId), ("$pri", priority), ("$t", now));
        Exec(connection, tx,
            """
            INSERT INTO blockers (id, project_id, summary, status, created_at, updated_at)
            VALUES ($id, $p, 'Permit pending', 'open', $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$p", projectId), ("$t", now));
        tx.Commit();
    }

    private string CopyFixture(string name)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Calendar", "Fixtures", name);
        if (!File.Exists(src))
        {
            // Fall back to repo-relative path during some runners.
            src = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "Calendar", "Fixtures", name));
        }

        Assert.True(File.Exists(src), "Missing fixture: " + src);
        var dest = Path.Combine(_root, name);
        File.Copy(src, dest, overwrite: true);
        return dest;
    }

    private static void Exec(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        cmd.ExecuteNonQuery();
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
