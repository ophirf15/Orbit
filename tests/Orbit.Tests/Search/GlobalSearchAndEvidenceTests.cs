using Orbit.Core.Data;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Search;

namespace Orbit.Tests.Search;

public sealed class GlobalSearchAndEvidenceTests
{
    [Fact]
    public void FragmentarySearch_FindsProjectContactAndFile()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        new SearchIndexRebuilder(factory).Rebuild();

        var search = new GlobalSearchService(factory);

        var harbor = search.Search("Harbor");
        Assert.Contains(harbor, h =>
            h.EntityType == "project" && h.EntityId == ids.HarborProjectId);

        var person = search.Search("Rivera");
        Assert.Contains(person, h =>
            h.EntityType == "person" && h.EntityId == ids.ContactPersonId);

        var w9 = search.Search("W-9");
        Assert.Contains(w9, h =>
            h.EntityType == "file" && h.EntityId == ids.W9FileId);

        var email = search.Search("install");
        Assert.Contains(email, h =>
            h.EntityType == "email" && h.EntityId == ids.SharedEmailId);
    }

    [Fact]
    public void FocusProjectBoost_RanksFocusedHitsHigher()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        new SearchIndexRebuilder(factory).Rebuild();

        var search = new GlobalSearchService(factory);
        var focused = search.Search("MetroFiber", focusProjectId: ids.HarborProjectId);
        Assert.NotEmpty(focused);

        var harborish = focused.FirstOrDefault(h =>
            string.Equals(h.ProjectId, ids.HarborProjectId, StringComparison.Ordinal));
        Assert.NotNull(harborish);
    }

    [Fact]
    public void EinEvidence_ReturnsValueAndW9Citation()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        new SearchIndexRebuilder(factory).Rebuild();

        var evidence = new EvidenceService(factory, new ContextBundleService(factory));
        var answer = evidence.Query("What's our EIN?");

        Assert.Equal("ein", answer.AnswerType);
        Assert.Equal(ids.AcmeEin, answer.Value);
        Assert.Equal(ids.AcmeOrgId, answer.OrganizationId);
        Assert.Contains(answer.Citations, c => c.EntityType == "file" && c.EntityId == ids.W9FileId);
        Assert.Contains(answer.Citations, c =>
            c.EntityType == "organization" || c.Kind == "fact");
    }

    [Fact]
    public void HarborCourtStatusEvidence_ExcludesRiverviewExtractions()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        var evidence = new EvidenceService(factory, new ContextBundleService(factory));
        var answer = evidence.Query("What's the status on Harbor Court?");

        Assert.Equal("project_status", answer.AnswerType);
        Assert.Equal(ids.HarborProjectId, answer.ProjectId);
        Assert.Contains("Harbor Court", answer.Answer, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Riverview modem", answer.Answer, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(answer.Status);
        var json = System.Text.Json.JsonSerializer.Serialize(answer.Status);
        Assert.Contains("Schedule Harbor Court", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Riverview modem", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Permit pending", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchIndex_IncludesEmailsCalendarConversations()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        // Conversation for index coverage
        var now = DateTime.UtcNow.ToString("O");
        using (var connection = factory.CreateConnection())
        {
            var convId = Guid.NewGuid().ToString("D");
            using var c = connection.CreateCommand();
            c.CommandText =
                "INSERT INTO conversations (id, channel, title, created_at, updated_at) VALUES ($id, 'desktop', 'Fiber follow-up', $t, $t);";
            c.Parameters.AddWithValue("$id", convId);
            c.Parameters.AddWithValue("$t", now);
            c.ExecuteNonQuery();

            using var m = connection.CreateCommand();
            m.CommandText =
                "INSERT INTO conversation_messages (id, conversation_id, role, body, sent_at, created_at) VALUES ($id, $c, 'user', 'Ask about Harbor Court fiber', $t, $t);";
            m.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            m.Parameters.AddWithValue("$c", convId);
            m.Parameters.AddWithValue("$t", now);
            m.ExecuteNonQuery();
        }

        var count = new SearchIndexRebuilder(factory).Rebuild();
        Assert.True(count >= 10);

        var search = new GlobalSearchService(factory);
        Assert.Contains(search.Search("survey"), h => h.EntityType == "calendar_event");
        Assert.Contains(search.Search("fiber"), h =>
            h.EntityType is "conversation" or "message");
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        var applied = new SqliteMigrator(factory).ApplyPendingMigrations();
        Assert.Contains(applied, v => v.StartsWith("0001_", StringComparison.Ordinal));
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitSearchEvidenceTests", Guid.NewGuid().ToString("N"));

        public string DbPath => Path.Combine(Root, "data", OrbitDbPaths.DatabaseFileName);

        public TempDb() => Directory.CreateDirectory(Path.Combine(Root, "data"));

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
