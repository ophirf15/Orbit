using Microsoft.Data.Sqlite;
using Orbit.Core.Data;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Pulse;
using Orbit.Infrastructure.Suggestions;

namespace Orbit.Tests.Projects;

public sealed class ProjectMergeAndProvenanceTests
{
    [Fact]
    public void Merge_moves_rows_transfers_aliases_and_archives_source()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var mutations = new OrbitMutationStore(factory);
        var merge = new ProjectMergeStore(factory);

        var source = projects.Create("Duplicate Site");
        var target = projects.Create("Canonical Site");
        projects.AddAlias(source.Id, "DupNick");
        var task = mutations.CreateTask("Follow up", source.Id, TaskStatuses.Active, "test", nextAction: "Call");
        InsertNote(factory, source.Id, "scratch note");

        InsertEmailLink(factory, "email-1", source.Id, confidence: 0.9, reason: "alias");
        InsertFileLink(factory, "file-1", source.Id);

        var preview = merge.Preview(source.Id, target.Id);
        Assert.Equal(1, preview.TaskCount);
        Assert.Equal(1, preview.NoteCount);
        Assert.Equal(1, preview.AliasCount);
        Assert.Equal(1, preview.EmailLinkCount);
        Assert.Equal(1, preview.FileLinkCount);

        var result = merge.Merge(source.Id, target.Id, force: true, actor: "test");
        Assert.True(result.ArchivedSource);
        Assert.Equal(1, result.Moved.Tasks);
        Assert.Equal(1, result.Moved.Notes);
        Assert.Equal(1, result.Moved.Aliases);

        using var connection = factory.CreateConnection();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id FROM tasks WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", task.Id);
            Assert.Equal(target.Id, (string)cmd.ExecuteScalar()!);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id FROM project_aliases WHERE normalized_alias = 'dupnick';";
            Assert.Equal(target.Id, (string)cmd.ExecuteScalar()!);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT archived_at IS NOT NULL FROM projects WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", source.Id);
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT 1 FROM project_aliases
                WHERE project_id = $p AND normalized_alias = 'duplicate site'
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$p", target.Id);
            Assert.NotNull(cmd.ExecuteScalar());
        }

        using (var audit = connection.CreateCommand())
        {
            audit.CommandText =
                """
                SELECT 1 FROM audit_events
                WHERE entity_id = $id AND event_type = 'project.merged'
                LIMIT 1;
                """;
            audit.Parameters.AddWithValue("$id", source.Id);
            Assert.NotNull(audit.ExecuteScalar());
        }
    }

    [Fact]
    public void Email_link_and_duty_task_persist_match_reason_and_confidence()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        var projects = new ProjectWriteStore(factory);
        var emails = new EmailArtifactStore(factory);
        var mutations = new OrbitMutationStore(factory);
        var threads = new TaskEmailThreadStore(factory);
        var duty = new EmailDutyEnsureService(factory, emails, threads, mutations);

        var project = projects.Create("Acme Widget Co");
        projects.AddAlias(project.Id, "Widget");

        var emailId = Guid.NewGuid().ToString("D");
        InsertBareEmail(factory, emailId, "Widget install", "Please schedule Widget this week.");

        var ensured = duty.Ensure(emailId);
        Assert.True(ensured.Ok);
        Assert.Equal(project.Id, ensured.ProjectId);
        Assert.False(string.IsNullOrWhiteSpace(ensured.TaskId));

        using var connection = factory.CreateConnection();
        using (var link = connection.CreateCommand())
        {
            link.CommandText =
                """
                SELECT confidence, match_reason FROM email_project_links
                WHERE email_artifact_id = $e AND project_id = $p;
                """;
            link.Parameters.AddWithValue("$e", emailId);
            link.Parameters.AddWithValue("$p", project.Id);
            using var reader = link.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.GetDouble(0) >= 0.8);
            Assert.Equal("alias", reader.GetString(1));
        }

        using (var task = connection.CreateCommand())
        {
            task.CommandText =
                """
                SELECT source_kind, source_confidence, source_match_reason
                FROM tasks WHERE id = $id;
                """;
            task.Parameters.AddWithValue("$id", ensured.TaskId!);
            using var reader = task.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("email", reader.GetString(0));
            Assert.True(reader.GetDouble(1) >= 0.8);
            Assert.Equal("alias", reader.GetString(2));
        }
    }

    [Fact]
    public void Pulse_surfaces_pending_disambiguate_email_as_unmatched_mail()
    {
        using var temp = new TempDb();
        var factory = OpenMigrated(temp);
        _ = new ProjectWriteStore(factory).Create("Harbor Court");
        var suggestions = new SuggestionStore(factory);
        var created = suggestions.Create(new CreateSuggestionRequest
        {
            SuggestionType = SuggestionTypes.DisambiguateEmailClaim,
            Summary = "Ambiguous mail needs a project",
            PayloadJson = """{"emailId":"email-xyz","candidates":[]}""",
            Confidence = 0.4,
        });

        var pulse = new PulseReadStore(factory).GetPulse();
        Assert.Contains(pulse.UnmatchedMail, m => m.SuggestionId == created.Id && m.EmailId == "email-xyz");
    }

    private static void InsertBareEmail(SqliteConnectionFactory factory, string id, string subject, string preview)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_artifacts (id, subject, sent_at, body_preview, created_at, updated_at)
            VALUES ($id, $subject, $t, $preview, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$subject", subject);
        cmd.Parameters.AddWithValue("$preview", preview);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static void InsertNote(SqliteConnectionFactory factory, string projectId, string text)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO notes (id, project_id, original_text, is_limbo, created_at, updated_at)
            VALUES ($id, $p, $text, 0, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private static void InsertEmailLink(
        SqliteConnectionFactory factory,
        string emailId,
        string projectId,
        double confidence,
        string reason)
    {
        InsertBareEmail(factory, emailId, "Subject", "Body");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at, confidence, match_reason)
            VALUES ($id, $email, $project, $t, $c, $r);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.Parameters.AddWithValue("$c", confidence);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.ExecuteNonQuery();
    }

    private static void InsertFileLink(SqliteConnectionFactory factory, string fileId, string projectId)
    {
        var now = DateTime.UtcNow.ToString("O");
        using var connection = factory.CreateConnection();
        using (var file = connection.CreateCommand())
        {
            file.CommandText =
                """
                INSERT INTO file_artifacts (id, path, display_name, created_at, updated_at)
                VALUES ($id, $path, $name, $t, $t);
                """;
            file.Parameters.AddWithValue("$id", fileId);
            file.Parameters.AddWithValue("$path", $@"C:\temp\{fileId}.txt");
            file.Parameters.AddWithValue("$name", "note.txt");
            file.Parameters.AddWithValue("$t", now);
            file.ExecuteNonQuery();
        }

        using var link = connection.CreateCommand();
        link.CommandText =
            """
            INSERT INTO file_project_links (id, file_artifact_id, project_id, created_at)
            VALUES ($id, $file, $project, $t);
            """;
        link.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        link.Parameters.AddWithValue("$file", fileId);
        link.Parameters.AddWithValue("$project", projectId);
        link.Parameters.AddWithValue("$t", now);
        link.ExecuteNonQuery();
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
            Path.Combine(Path.GetTempPath(), "OrbitMergeTests", Guid.NewGuid().ToString("N"));

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
