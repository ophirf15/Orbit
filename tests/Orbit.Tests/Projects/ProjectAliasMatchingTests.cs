using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Projects;

public sealed class ProjectAliasMatchingTests
{
    [Fact]
    public void Normalize_collapses_punctuation_and_case()
    {
        Assert.Equal("acme widget co", ProjectIdentityMatcher.Normalize("  Acme-Widget, Co. "));
        Assert.Equal("widget", ProjectIdentityMatcher.Normalize("Widget"));
    }

    [Fact]
    public void Alias_Widget_matches_Acme_Widget_Co_in_email_haystack()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var created = projects.Create("Acme Widget Co", summary: "Generic fixture");
        projects.AddAlias(created.Id, "Widget");

        var matches = EmailProjectAutoLinker.MatchProjects(
            factory,
            subject: "Please schedule Widget install",
            bodyPreview: "Need to confirm the Widget site visit.");

        Assert.Contains(matches, m => m.ProjectId == created.Id && m.Confidence >= 0.8);
    }

    [Fact]
    public void ClaimSplitter_links_on_alias_and_populates_candidates_when_ambiguous()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var acme = projects.Create("Acme Widget Co");
        projects.AddAlias(acme.Id, "Widget");
        var other = projects.Create("Harbor Court");

        InsertBareEmail(factory, "email-alias-1", "Widget order", "Please order parts for Widget.");
        var splitter = new MultiProjectClaimSplitter(factory, new SuggestionStore(factory));
        var hard = splitter.ProcessEmail("email-alias-1", "Please order parts for Widget.", "Widget order");
        Assert.Contains(acme.Id, hard.MentionedProjectIds);
        Assert.False(hard.WasAmbiguous);

        InsertBareEmail(factory, "email-amb-1", "Need action", "Please follow up on the outstanding item.");
        var amb = splitter.ProcessEmail("email-amb-1", "Please follow up on the outstanding item.", "Need action");
        Assert.True(amb.WasAmbiguous);
        Assert.False(string.IsNullOrWhiteSpace(amb.SuggestionId));

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT payload_json FROM agent_suggestions WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", amb.SuggestionId!);
        var payload = (string)cmd.ExecuteScalar()!;
        Assert.Contains("candidates", payload, StringComparison.OrdinalIgnoreCase);
        _ = other;
    }

    [Fact]
    public void Create_refuses_near_duplicate_of_name_or_alias()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var existing = projects.Create("Acme Widget Co");
        projects.AddAlias(existing.Id, "Widget");

        var byName = projects.FindCreateConflicts("Acme Widget Co");
        Assert.Contains(byName, c => c.ProjectId == existing.Id && c.Score >= ProjectIdentityMatcher.NearDupeThreshold);

        var byAlias = projects.FindCreateConflicts("Widget");
        Assert.Contains(byAlias, c => c.ProjectId == existing.Id && c.Score >= ProjectIdentityMatcher.NearDupeThreshold);

        Assert.Empty(projects.FindCreateConflicts("Completely Unrelated Site"));
    }

    [Fact]
    public void Alias_is_globally_unique()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var a = projects.Create("Acme Widget Co");
        var b = projects.Create("Other Co");
        projects.AddAlias(a.Id, "Widget");
        Assert.Throws<InvalidOperationException>(() => projects.AddAlias(b.Id, "widget"));
    }

    [Fact]
    public void UpdateTask_moves_between_projects_and_clears_workstream()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var mutations = new OrbitMutationStore(factory);
        var from = projects.Create("Acme Widget Co");
        var to = projects.Create("Harbor Court");
        var ws = mutations.CreateWorkstream(from.Id, "Ops");
        var task = mutations.CreateTask("Chase vendor", from.Id, null, "test", workstreamId: ws.Id);
        Assert.Equal(from.Id, task.ProjectId);
        Assert.Equal(ws.Id, task.WorkstreamId);

        var moved = mutations.UpdateTask(task.Id, null, null, null, "test", projectId: to.Id);
        Assert.Equal(to.Id, moved.ProjectId);
        Assert.Null(moved.WorkstreamId);

        using var connection = factory.CreateConnection();
        using var audit = connection.CreateCommand();
        audit.CommandText =
            """
            SELECT 1 FROM audit_events
            WHERE entity_id = $id AND event_type = 'task.moved'
            LIMIT 1;
            """;
        audit.Parameters.AddWithValue("$id", task.Id);
        Assert.NotNull(audit.ExecuteScalar());
    }

    [Fact]
    public void Update_project_code_and_list_aliases()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var created = projects.Create("Acme Widget Co");
        var updated = projects.Update(created.Id, null, null, "AWC", touchCode: true);
        Assert.Equal("AWC", updated.Code);
        projects.AddAlias(created.Id, "Widget");
        var aliases = projects.ListAliases(created.Id);
        Assert.Single(aliases);
        Assert.Equal("Widget", aliases[0].Alias);
        Assert.True(projects.RemoveAlias(created.Id, "Widget"));
        Assert.Empty(projects.ListAliases(created.Id));
    }

    private static void InsertBareEmail(SqliteConnectionFactory factory, string emailId, string subject, string body)
    {
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        var now = DateTime.UtcNow.ToString("O");
        cmd.CommandText =
            """
            INSERT INTO email_artifacts (id, subject, sent_at, body_preview, created_at, updated_at)
            VALUES ($id, $subject, $t, $body, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        cmd.Parameters.AddWithValue("$subject", subject);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(OrbitDbPaths.GetDatabasePath(temp.DataRoot));
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string DataRoot { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitAliasTests", Guid.NewGuid().ToString("N"));

        public TempDb() => Directory.CreateDirectory(DataRoot);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
                }
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }
}
