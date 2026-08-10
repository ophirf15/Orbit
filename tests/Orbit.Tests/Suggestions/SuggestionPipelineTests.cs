using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Data.Demo;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Suggestions;

public sealed class SuggestionPipelineTests
{
    [Fact]
    public void CaptureMatchingProject_AutoAssignsHighConfidence()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var suggestions = new SuggestionStore(factory);
        var engine = new SuggestionEngine(factory, suggestions);

        var capture = notes.CreateCapture("Follow up with Harbor Court about fiber", projectId: null);
        var created = engine.ProcessNoteCreated(capture.NoteId);

        var assign = Assert.Single(created, s => s.SuggestionType == SuggestionTypes.AssignToProject);
        Assert.Equal(SuggestionStatuses.Accepted, assign.Status);
        Assert.Equal(ids.HarborProjectId, assign.ProjectId);
        Assert.Equal(capture.NoteId, assign.NoteId);
        Assert.Contains("Harbor Court", assign.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaptureWithoutMatch_DoesNotCreateReviewLimboChore()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var suggestions = new SuggestionStore(factory);
        var engine = new SuggestionEngine(factory, suggestions);

        var capture = notes.CreateCapture("Buy milk and eggs", projectId: null);
        var created = engine.ProcessNoteCreated(capture.NoteId);

        Assert.Empty(created);
        Assert.Empty(suggestions.List(SuggestionStatuses.Pending)
            .Where(s => s.SuggestionType == SuggestionTypes.ReviewLimbo));
    }

    [Fact]
    public void AcceptAssign_UpdatesNoteProject_PreservesOriginalText()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var suggestions = new SuggestionStore(factory);
        var engine = new SuggestionEngine(factory, suggestions);

        const string text = "Schedule Harbor Court walkthrough";
        var capture = notes.CreateCapture(text, projectId: null);
        var suggestion = Assert.Single(engine.ProcessNoteCreated(capture.NoteId));
        Assert.Equal(SuggestionStatuses.Accepted, suggestion.Status);

        using var connection = factory.CreateConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT original_text, project_id, is_limbo FROM notes WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", capture.NoteId);
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(text, reader.GetString(0));
            Assert.Equal(ids.HarborProjectId, reader.GetString(1));
            Assert.Equal(0, reader.GetInt32(2));
        }

        using (var audit = connection.CreateCommand())
        {
            audit.CommandText =
                "SELECT COUNT(*) FROM audit_events WHERE event_type = 'suggestion.accepted' AND entity_id = $id;";
            audit.Parameters.AddWithValue("$id", suggestion.Id);
            Assert.Equal(1, Convert.ToInt32(audit.ExecuteScalar()));
        }
    }

    [Fact]
    public void Reject_SetsStatusAndAudits()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var ids = new DemoGraphSeed(factory).Seed();
        var notes = new NoteWriteStore(factory);
        var suggestions = new SuggestionStore(factory);

        var capture = notes.CreateCapture("Random limbo thought", projectId: null);
        var suggestion = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.AssignToProject,
            Summary = "Maybe assign",
            PayloadJson = "{\"action\":\"assign_to_project\",\"noteId\":\"" + capture.NoteId
                + "\",\"projectId\":\"" + ids.HarborProjectId + "\"}",
            NoteId = capture.NoteId,
            ProjectId = ids.HarborProjectId,
            Confidence = 0.5,
        });
        var rejected = suggestions.Reject(suggestion.Id, actor: "user");
        Assert.Equal(SuggestionStatuses.Rejected, rejected.Status);

        using var connection = factory.CreateConnection();
        using var audit = connection.CreateCommand();
        audit.CommandText =
            "SELECT COUNT(*) FROM audit_events WHERE event_type = 'suggestion.rejected' AND entity_id = $id;";
        audit.Parameters.AddWithValue("$id", suggestion.Id);
        Assert.Equal(1, Convert.ToInt32(audit.ExecuteScalar()));
    }

    private static SqliteConnectionFactory OpenMigrated(TempDb temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new SqliteMigrator(factory).ApplyPendingMigrations();
        return factory;
    }

    private sealed class TempDb : IDisposable
    {
        public string Root { get; } =
            Path.Combine(Path.GetTempPath(), "OrbitSuggestionTests", Guid.NewGuid().ToString("N"));

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
