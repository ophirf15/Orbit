using Orbit.Core.Data;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Pulse;

namespace Orbit.Tests.Projects;

public sealed class ProjectDossierAndCalendarToggleTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "OrbitDossierTests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;

    public ProjectDossierAndCalendarToggleTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        _factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(Path.Combine(_root, "data")));
        new SqliteMigrator(_factory).ApplyPendingMigrations();
    }

    [Fact]
    public void Dossier_RoundTrips_AndFlagsEmpty()
    {
        var projects = new ProjectWriteStore(_factory);
        var created = projects.Create("Acme Widget Co", summary: "Free-text summary stays separate");

        var empty = projects.GetDossier(created.Id);
        Assert.True(empty.IsStructurallyEmpty);

        var updated = projects.UpdateDossier(created.Id, new ProjectDossierPatch
        {
            Address = "100 Main St",
            OwnerClient = "Acme Holdings",
            Phase = "Stabilization",
            Portfolio = "West",
            CurrentPriorities = ["Close roof bid", "Chase CO"],
            CriticalContacts =
            [
                new ProjectDossierContact { Name = "Pat Vendor", Role = "GC", Contact = "pat@example.com" },
            ],
        });

        Assert.False(updated.IsStructurallyEmpty);
        Assert.Equal("100 Main St", updated.Address);
        Assert.Equal(2, updated.CurrentPriorities.Count);

        var read = new ProjectReadStore(_factory).Get(created.Id);
        Assert.NotNull(read);
        Assert.False(read!.DossierEmpty);
        Assert.Equal("Acme Holdings", read.Dossier?.OwnerClient);

        var cleared = projects.UpdateDossier(created.Id, new ProjectDossierPatch
        {
            Address = "",
            OwnerClient = "",
            Phase = "",
            Portfolio = "",
            CurrentPriorities = [],
            CriticalContacts = [],
        });
        Assert.True(cleared.IsStructurallyEmpty);
    }

    [Fact]
    public async Task Calendar_EnabledToggle_FiltersUpcomingContext()
    {
        var path = CopyFixture("harbor-mailbox.ics");
        var sync = new CalendarSyncService(
            _factory,
            providersFactory: () => [new IcsCalendarProvider(path, "Harbor Court ICS")]);
        await sync.SyncAsync();

        var store = new CalendarReadStore(_factory);
        var sources = store.ListSources().Where(s => s.Provider == CalendarProviders.Ics).ToList();
        Assert.NotEmpty(sources);
        var source = sources[0];
        Assert.True(source.Enabled);

        // Seed an imminent event so GetUpcomingContext can see it regardless of fixture dates.
        using (var connection = _factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            var start = DateTime.UtcNow.AddHours(6).ToString("O");
            var end = DateTime.UtcNow.AddHours(7).ToString("O");
            cmd.CommandText =
                """
                INSERT INTO calendar_events (
                  id, calendar_source_id, title, starts_at, ends_at, location,
                  external_uid, body_preview, organizer, attention_score, created_at, updated_at)
                VALUES (
                  $id, $source, $title, $start, $end, NULL,
                  $uid, NULL, NULL, NULL, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$source", source.Id);
            cmd.Parameters.AddWithValue("$title", "Widget walkthrough");
            cmd.Parameters.AddWithValue("$start", start);
            cmd.Parameters.AddWithValue("$end", end);
            cmd.Parameters.AddWithValue("$uid", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var before = store.GetUpcomingContext(TimeSpan.FromDays(7), limit: 40);
        Assert.Contains(before, m => m.Title.Contains("Widget", StringComparison.OrdinalIgnoreCase));

        store.SetEnabled(source.Id, enabled: false);
        var after = store.GetUpcomingContext(TimeSpan.FromDays(7), limit: 40);
        Assert.DoesNotContain(after, m => string.Equals(m.SourceId, source.Id, StringComparison.Ordinal));

        store.SetEnabled(source.Id, enabled: true);
        var restored = store.GetUpcomingContext(TimeSpan.FromDays(7), limit: 40);
        Assert.Contains(restored, m => string.Equals(m.SourceId, source.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void OrbitProjects_Surface_DossierAndNextActionFlags()
    {
        var projects = new ProjectWriteStore(_factory);
        var created = projects.Create("Acme Site A");
        new PulseReadStore(_factory).SetInOrbit(created.Id, true);

        using (var connection = _factory.CreateConnection())
        using (var cmd = connection.CreateCommand())
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            cmd.CommandText =
                """
                INSERT INTO tasks (id, project_id, title, status, next_action, body, created_at, updated_at)
                VALUES ($id, $p, 'Chase bid', 'active', NULL, NULL, $t, $t);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$p", created.Id);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.ExecuteNonQuery();
        }

        var orbit = new PulseReadStore(_factory).ListOrbitProjects();
        var row = Assert.Single(orbit);
        Assert.True(row.DossierEmpty);
        Assert.True(row.MissingNextAction);
    }

    private string CopyFixture(string name)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Calendar", "Fixtures", name);
        if (!File.Exists(src))
        {
            src = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "Calendar", "Fixtures", name));
        }

        Directory.CreateDirectory(Path.Combine(_root, "ics"));
        var dest = Path.Combine(_root, "ics", name);
        File.Copy(src, dest, overwrite: true);
        return dest;
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
        catch
        {
            // ignore cleanup races on Windows
        }
    }
}
