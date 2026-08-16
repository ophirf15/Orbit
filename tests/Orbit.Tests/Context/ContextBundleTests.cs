using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Context;

public sealed class ContextBundleTests
{
    [Fact]
    public void HarborCourtBundle_ExcludesRiverviewExtractionSummaries()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var bundles = new ContextBundleService(factory);

        var harbor = bundles.GetBundle(ContextTargetTypes.Project, ids.HarborProjectId);
        Assert.NotNull(harbor);

        var summaries = harbor!.Emails
            .SelectMany(e => e.Extractions)
            .Select(x => x.Summary)
            .ToList();

        Assert.Contains(summaries, s => s.Contains("Harbor Court", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(summaries, s => s.Contains("Riverview", StringComparison.OrdinalIgnoreCase));
        Assert.All(harbor.Emails.SelectMany(e => e.Extractions), x =>
            Assert.Equal(ids.HarborProjectId, x.ProjectId));
    }

    [Fact]
    public void SharedEmail_HasTwoExtractions_WithDistinctProjectIds()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id, summary FROM email_extractions
            WHERE email_artifact_id = $e AND archived_at IS NULL
            ORDER BY project_id;
            """;
        cmd.Parameters.AddWithValue("$e", ids.SharedEmailId);

        var rows = new List<(string ProjectId, string Summary)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ProjectId == ids.HarborProjectId);
        Assert.Contains(rows, r => r.ProjectId == ids.RiverviewProjectId);
        Assert.NotEqual(rows[0].Summary, rows[1].Summary);
    }

    [Fact]
    public void MetroFiberAppearsInBothBundles_WithoutMergingTaskContexts()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var bundles = new ContextBundleService(factory);

        var harbor = bundles.GetBundle(ContextTargetTypes.Project, ids.HarborProjectId);
        var riverview = bundles.GetBundle(ContextTargetTypes.Project, ids.RiverviewProjectId);
        Assert.NotNull(harbor);
        Assert.NotNull(riverview);

        Assert.Contains(harbor!.RelatedEntities, r =>
            r.EntityType == EntityTypes.Organization
            && r.EntityId == ids.MetroFiberOrgId
            && r.Label.Contains("MetroFiber", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(riverview!.RelatedEntities, r =>
            r.EntityType == EntityTypes.Organization
            && r.EntityId == ids.MetroFiberOrgId);

        Assert.Contains(harbor.Tasks, t => t.TaskId == ids.HarborTaskId);
        Assert.DoesNotContain(harbor.Tasks, t => t.TaskId == ids.RiverviewTaskId);
        Assert.Contains(riverview.Tasks, t => t.TaskId == ids.RiverviewTaskId);
        Assert.DoesNotContain(riverview.Tasks, t => t.TaskId == ids.HarborTaskId);
    }

    [Fact]
    public void Bundle_IncludesDemoHarborCourtMeeting_FromCalendarLinks()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var bundle = new ContextBundleService(factory).GetBundle(ContextTargetTypes.Project, ids.HarborProjectId);
        Assert.NotNull(bundle);
        Assert.Contains(bundle!.Meetings, m => m.Title.Contains("MetroFiber", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClaimSplitter_DualProjectBody_CreatesSeparateExtractions()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        var emailId = Guid.NewGuid().ToString("D");
        InsertBareEmail(factory, emailId, "Dual site MetroFiber");

        var splitter = new MultiProjectClaimSplitter(factory, new SuggestionStore(factory));
        var result = splitter.ProcessEmail(
            emailId,
            "Please schedule Harbor Court install on Monday. Also confirm Riverview modem before Friday.");

        Assert.Equal(2, result.MentionedProjectIds.Count);
        Assert.Contains(ids.HarborProjectId, result.MentionedProjectIds);
        Assert.Contains(ids.RiverviewProjectId, result.MentionedProjectIds);
        Assert.Equal(2, result.CreatedExtractionIds.Count);
        Assert.False(result.WasAmbiguous);

        // Second pass must not overwrite / duplicate per-project rows
        var again = splitter.ProcessEmail(
            emailId,
            "Please schedule Harbor Court install on Monday. Also confirm Riverview modem before Friday.");
        Assert.Empty(again.CreatedExtractionIds);

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*), COUNT(DISTINCT project_id) FROM email_extractions WHERE email_artifact_id = $e;";
        cmd.Parameters.AddWithValue("$e", emailId);
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
    }

    [Fact]
    public void ClaimSplitter_AmbiguousBody_CreatesSuggestion_NotExtraction()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        _ = new DemoGraphSeed(factory).Seed();

        var emailId = Guid.NewGuid().ToString("D");
        InsertBareEmail(factory, emailId, "Need schedule");

        var splitter = new MultiProjectClaimSplitter(factory, new SuggestionStore(factory));
        var result = splitter.ProcessEmail(
            emailId,
            "Please schedule the install and confirm the modem model with MetroFiber.");

        Assert.Empty(result.MentionedProjectIds);
        Assert.Empty(result.CreatedExtractionIds);
        Assert.True(result.WasAmbiguous);
        Assert.False(string.IsNullOrWhiteSpace(result.SuggestionId));

        using var connection = factory.CreateConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM email_extractions WHERE email_artifact_id = $e;";
            cmd.Parameters.AddWithValue("$e", emailId);
            Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT suggestion_type, status FROM agent_suggestions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", result.SuggestionId!);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(SuggestionTypes.DisambiguateEmailClaim, reader.GetString(0));
            Assert.Equal(SuggestionStatuses.Pending, reader.GetString(1));
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT summary, payload_json FROM agent_suggestions WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", result.SuggestionId!);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            var summary = reader.GetString(0);
            var payload = reader.GetString(1);
            Assert.Contains("Ambiguous email", summary, StringComparison.Ordinal);
            Assert.Contains("Need schedule", summary, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"subject\"", payload, StringComparison.Ordinal);
            Assert.Contains("\"snippet\"", payload, StringComparison.Ordinal);
            Assert.Contains("Please schedule", payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ClaimSplitter_ExplicitProjectLink_SkipsAmbiguousSuggestion()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();

        var emailId = Guid.NewGuid().ToString("D");
        InsertBareEmail(factory, emailId, "PG&E PMA");
        LinkEmailExplicit(factory, emailId, ids.HarborProjectId);

        var splitter = new MultiProjectClaimSplitter(factory, new SuggestionStore(factory));
        var result = splitter.ProcessEmail(
            emailId,
            "Please confirm the account and schedule service for next week.");

        Assert.False(result.WasAmbiguous);
        Assert.Null(result.SuggestionId);
        Assert.Contains(ids.HarborProjectId, result.LinkedProjectIds);

        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*) FROM agent_suggestions
            WHERE suggestion_type = $type AND payload_json LIKE $needle;
            """;
        cmd.Parameters.AddWithValue("$type", SuggestionTypes.DisambiguateEmailClaim);
        cmd.Parameters.AddWithValue("$needle", "%" + emailId + "%");
        Assert.Equal(0, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void SuggestMergesFromEmail_ExplicitProject_EmitsNothing()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        SeedOpenTask(factory, ids.HarborProjectId, "Comcast modem install");
        SeedOpenTask(factory, ids.HarborProjectId, "Send CAA lease files");

        var emailId = Guid.NewGuid().ToString("D");
        InsertBareEmail(factory, emailId, "Re: 707 Leahy PG&E PMA");
        LinkEmailExplicit(factory, emailId, ids.HarborProjectId);

        var engine = new TaskRelationshipEngine(factory, new SuggestionStore(factory));
        var created = engine.SuggestMergesFromEmail(
            emailId,
            "Please review the PG&E account and confirm utility billing for the property.");

        Assert.Empty(created);
    }

    [Fact]
    public void AcceptReject_MergeSuggestion_WritesOperatorMemory()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var memory = new OperatorMemoryStore(factory);
        var suggestions = new SuggestionStore(factory, memory);

        var taskId = SeedOpenTask(factory, ids.HarborProjectId, "Utility follow-up");
        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Email may answer Utility follow-up",
            PayloadJson = $$"""{"taskId":"{{taskId}}","text":"billing note","field":"body","sourceType":"email","sourceId":"e1"}""",
            ProjectId = ids.HarborProjectId,
            TaskId = taskId,
            Confidence = 0.5,
        });

        suggestions.Reject(suggestion.Id, actor: "test");
        var facts = EmailRelationMemory.ListRecentFactLines(memory, limit: 5);
        Assert.Contains(facts, f => f.Contains("NOT related", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(facts, f => f.Contains("suggestion-train", StringComparison.OrdinalIgnoreCase));

        var accept = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.MergeIntoTask,
            Summary = "Email may answer Utility follow-up again",
            PayloadJson = $$"""{"taskId":"{{taskId}}","text":"billing note 2","field":"body","sourceType":"email","sourceId":"e2"}""",
            ProjectId = ids.HarborProjectId,
            TaskId = taskId,
            Confidence = 0.55,
        });
        suggestions.Accept(accept.Id, actor: "test");
        facts = EmailRelationMemory.ListRecentFactLines(memory, limit: 10);
        Assert.Contains(facts, f => f.Contains("related to task", StringComparison.OrdinalIgnoreCase)
                                    && !f.Contains("NOT related", StringComparison.Ordinal));
    }

    [Fact]
    public void AcceptReject_AssignSuggestion_WritesTrainingMemory()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var memory = new OperatorMemoryStore(factory);
        var suggestions = new SuggestionStore(factory, memory);

        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.AssignToProject,
            Summary = "Note belongs on Harbor",
            PayloadJson = """{"noteId":"n1","projectId":"p1"}""",
            ProjectId = ids.HarborProjectId,
            Confidence = 0.4,
        });

        suggestions.Reject(suggestion.Id, actor: "test");
        var facts = EmailRelationMemory.ListRecentFactLines(memory, limit: 5);
        Assert.Contains(facts, f => f.Contains("REJECTED", StringComparison.Ordinal)
                                    && f.Contains("assign_to_project", StringComparison.Ordinal));

        EmailRelationMemory.RememberAlways(memory, suggestion);
        facts = EmailRelationMemory.ListRecentFactLines(memory, limit: 10);
        Assert.Contains(facts, f => f.Contains("ALWAYS apply", StringComparison.OrdinalIgnoreCase));
    }

    private static void LinkEmailExplicit(SqliteConnectionFactory factory, string emailId, string projectId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at, confidence, match_reason)
            VALUES ($id, $email, $project, $t, 1.0, 'explicit');
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static string SeedOpenTask(SqliteConnectionFactory factory, string projectId, string title)
    {
        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tasks (id, project_id, title, status, next_action, created_at, updated_at)
            VALUES ($id, $project, $title, 'active', $title, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
        return id;
    }

    private static void InsertBareEmail(SqliteConnectionFactory factory, string emailId, string subject)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_artifacts (id, subject, sent_at, body_preview, created_at, updated_at)
            VALUES ($id, $subject, $t, $subject, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        cmd.Parameters.AddWithValue("$subject", subject);
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
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitContextBundle", Guid.NewGuid().ToString("N"));

        public string DataRoot => Path.Combine(Root, "data");

        public TempDb() => Directory.CreateDirectory(DataRoot);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
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
