using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data.Demo;

/// <summary>
/// Seeds Harbor Court + Riverview both served by MetroFiber without merging task contexts.
/// </summary>
public sealed class DemoGraphSeed
{
    private readonly SqliteConnectionFactory _factory;

    public DemoGraphSeed(SqliteConnectionFactory factory) => _factory = factory;

    public DemoGraphIds SeedIfEmpty()
    {
        using var connection = _factory.CreateConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM projects;";
            var count = Convert.ToInt32(check.ExecuteScalar());
            if (count > 0)
            {
                return DemoGraphIds.ReadExisting(connection);
            }
        }

        return Seed(connection);
    }

    public DemoGraphIds Seed()
    {
        using var connection = _factory.CreateConnection();
        return Seed(connection);
    }

    private DemoGraphIds Seed(SqliteConnection connection)
    {
        var now = DateTime.UtcNow.ToString("O");
        var ids = DemoGraphIds.CreateNew();

        using var tx = connection.BeginTransaction();

        Exec(connection, tx,
            "INSERT INTO organizations (id, name, kind, notes, created_at, updated_at) VALUES ($id, $name, 'vendor', 'Shared ISP', $t, $t);",
            ("$id", ids.MetroFiberOrgId), ("$name", "MetroFiber"), ("$t", now));

        Exec(connection, tx,
            "INSERT INTO projects (id, name, code, summary, status, created_at, updated_at) VALUES ($id, $name, $code, $summary, 'active', $t, $t);",
            ("$id", ids.HarborProjectId), ("$name", "Harbor Court"), ("$code", "HARBOR"),
            ("$summary", "Harbor Court property"), ("$t", now));

        Exec(connection, tx,
            "INSERT INTO projects (id, name, code, summary, status, created_at, updated_at) VALUES ($id, $name, $code, $summary, 'active', $t, $t);",
            ("$id", ids.RiverviewProjectId), ("$name", "Riverview"), ("$code", "RIVER"),
            ("$summary", "Second property"), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO workstreams (id, project_id, name, status, priority, next_action, created_at, updated_at)
            VALUES ($id, $project, 'Internet Setup', 'active', 2, 'Schedule install', $t, $t);
            """,
            ("$id", ids.HarborInternetWorkstreamId), ("$project", ids.HarborProjectId), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO workstreams (id, project_id, name, status, priority, next_action, created_at, updated_at)
            VALUES ($id, $project, 'Internet Setup', 'active', 2, 'Confirm modem', $t, $t);
            """,
            ("$id", ids.RiverviewInternetWorkstreamId), ("$project", ids.RiverviewProjectId), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO tasks (id, project_id, workstream_id, title, status, priority, next_action, due_at, created_at, updated_at)
            VALUES ($id, $project, $ws, 'Order MetroFiber service', 'active', 1, 'Call MetroFiber', $due, $t, $t);
            """,
            ("$id", ids.HarborTaskId), ("$project", ids.HarborProjectId),
            ("$ws", ids.HarborInternetWorkstreamId), ("$due", DateTime.UtcNow.AddDays(3).ToString("O")), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO tasks (id, project_id, workstream_id, title, status, priority, next_action, due_at, created_at, updated_at)
            VALUES ($id, $project, $ws, 'Order MetroFiber service', 'not_started', 1, 'Email account manager', $due, $t, $t);
            """,
            ("$id", ids.RiverviewTaskId), ("$project", ids.RiverviewProjectId),
            ("$ws", ids.RiverviewInternetWorkstreamId), ("$due", DateTime.UtcNow.AddDays(7).ToString("O")), ("$t", now));

        // MetroFiber serves each project with distinct relationship context (no crosstalk).
        Exec(connection, tx,
            """
            INSERT INTO relationships (
              id, source_type, source_id, target_type, target_id, relationship_type,
              project_id, confidence, confirmed_by_user, created_by, created_at, updated_at)
            VALUES ($id, $st, $sid, $tt, $tid, $rt, $project, 0.95, 1, $by, $t, $t);
            """,
            ("$id", ids.MetroFiberServesHarborRelId),
            ("$st", EntityTypes.Organization), ("$sid", ids.MetroFiberOrgId),
            ("$tt", EntityTypes.Project), ("$tid", ids.HarborProjectId),
            ("$rt", RelationshipTypes.Serves), ("$project", ids.HarborProjectId),
            ("$by", CreatedByActors.User), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO relationships (
              id, source_type, source_id, target_type, target_id, relationship_type,
              project_id, confidence, confirmed_by_user, created_by, created_at, updated_at)
            VALUES ($id, $st, $sid, $tt, $tid, $rt, $project, 0.95, 1, $by, $t, $t);
            """,
            ("$id", ids.MetroFiberServesRiverviewRelId),
            ("$st", EntityTypes.Organization), ("$sid", ids.MetroFiberOrgId),
            ("$tt", EntityTypes.Project), ("$tid", ids.RiverviewProjectId),
            ("$rt", RelationshipTypes.Serves), ("$project", ids.RiverviewProjectId),
            ("$by", CreatedByActors.User), ("$t", now));

        Exec(connection, tx,
            "INSERT INTO people (id, display_name, given_name, family_name, created_at, updated_at) VALUES ($id, $name, 'Alex', 'Rivera', $t, $t);",
            ("$id", ids.ContactPersonId), ("$name", "Alex Rivera"), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO organization_memberships (id, person_id, organization_id, title, created_at, updated_at)
            VALUES ($id, $person, $org, 'Account manager', $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$person", ids.ContactPersonId),
            ("$org", ids.MetroFiberOrgId), ("$t", now));

        Exec(connection, tx,
            "INSERT INTO contact_methods (id, person_id, method_type, value, is_primary, created_at, updated_at) VALUES ($id, $person, 'email', 'alex.rivera@metrofiber.example', 1, $t, $t);",
            ("$id", Guid.NewGuid().ToString("D")), ("$person", ids.ContactPersonId), ("$t", now));

        // Contact involved in both project contexts
        Exec(connection, tx,
            """
            INSERT INTO relationships (
              id, source_type, source_id, target_type, target_id, relationship_type,
              project_id, confidence, confirmed_by_user, created_by, created_at, updated_at)
            VALUES ($id, $st, $sid, $tt, $tid, $rt, $project, 0.9, 1, $by, $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")),
            ("$st", EntityTypes.Person), ("$sid", ids.ContactPersonId),
            ("$tt", EntityTypes.Project), ("$tid", ids.HarborProjectId),
            ("$rt", RelationshipTypes.InvolvedIn), ("$project", ids.HarborProjectId),
            ("$by", CreatedByActors.User), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO relationships (
              id, source_type, source_id, target_type, target_id, relationship_type,
              project_id, confidence, confirmed_by_user, created_by, created_at, updated_at)
            VALUES ($id, $st, $sid, $tt, $tid, $rt, $project, 0.9, 1, $by, $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")),
            ("$st", EntityTypes.Person), ("$sid", ids.ContactPersonId),
            ("$tt", EntityTypes.Project), ("$tid", ids.RiverviewProjectId),
            ("$rt", RelationshipTypes.InvolvedIn), ("$project", ids.RiverviewProjectId),
            ("$by", CreatedByActors.User), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO email_artifacts (id, subject, sent_at, body_preview, created_at, updated_at)
            VALUES ($id, 'MetroFiber install windows', $sent, $preview, $t, $t);
            """,
            ("$id", ids.SharedEmailId), ("$sent", now), ("$t", now),
            ("$preview",
                "Harbor Court: Schedule install next week. Riverview: Confirm modem model before truck roll. Shared MetroFiber thread."));

        Exec(connection, tx,
            "INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at) VALUES ($id, $email, $project, $t);",
            ("$id", Guid.NewGuid().ToString("D")), ("$email", ids.SharedEmailId),
            ("$project", ids.HarborProjectId), ("$t", now));

        Exec(connection, tx,
            "INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at) VALUES ($id, $email, $project, $t);",
            ("$id", Guid.NewGuid().ToString("D")), ("$email", ids.SharedEmailId),
            ("$project", ids.RiverviewProjectId), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO email_extractions (id, email_artifact_id, extraction_type, summary, project_id, workstream_id, confidence, created_at, updated_at)
            VALUES ($id, $email, 'action', 'Schedule Harbor Court install', $project, $ws, 0.8, $t, $t);
            """,
            ("$id", ids.HarborExtractionId), ("$email", ids.SharedEmailId),
            ("$project", ids.HarborProjectId), ("$ws", ids.HarborInternetWorkstreamId), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO email_extractions (id, email_artifact_id, extraction_type, summary, project_id, workstream_id, confidence, created_at, updated_at)
            VALUES ($id, $email, 'action', 'Confirm Riverview modem model', $project, $ws, 0.8, $t, $t);
            """,
            ("$id", ids.RiverviewExtractionId), ("$email", ids.SharedEmailId),
            ("$project", ids.RiverviewProjectId), ("$ws", ids.RiverviewInternetWorkstreamId), ("$t", now));

        // Waiting line + open blocker for Harbor Court (workbench indicators).
        Exec(connection, tx,
            """
            INSERT INTO tasks (id, project_id, workstream_id, title, status, priority, next_action, waiting_on_person_id, created_at, updated_at)
            VALUES ($id, $project, $ws, 'Confirm install window', 'waiting', 2, 'Wait for Alex', $person, $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$project", ids.HarborProjectId),
            ("$ws", ids.HarborInternetWorkstreamId), ("$person", ids.ContactPersonId), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO blockers (id, project_id, workstream_id, task_id, summary, status, created_at, updated_at)
            VALUES ($id, $project, $ws, $task, 'Permit pending from HOA', 'open', $t, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$project", ids.HarborProjectId),
            ("$ws", ids.HarborInternetWorkstreamId), ("$task", ids.HarborTaskId), ("$t", now));

        // Limbo capture + agent suggestion that must not rewrite original text.
        Exec(connection, tx,
            """
            INSERT INTO notes (id, project_id, workstream_id, task_id, original_text, is_limbo, created_at, updated_at)
            VALUES ($id, NULL, NULL, NULL, $text, 1, $t, $t);
            """,
            ("$id", ids.LimboNoteId), ("$text", "Call him back about proposal"), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO agent_suggestions (
              id, suggestion_type, summary, payload_json, project_id, note_id, status, confidence, created_at, updated_at)
            VALUES (
              $id, 'assign_to_project', 'Maybe assign to Harbor Court', $payload, $project, $note, 'pending', 0.55, $t, $t);
            """,
            ("$id", ids.LimboSuggestionId),
            ("$payload",
                "{\"action\":\"assign_to_project\",\"noteId\":\"" + ids.LimboNoteId
                + "\",\"projectId\":\"" + ids.HarborProjectId + "\",\"explanation\":\"Demo seed\"}"),
            ("$project", ids.HarborProjectId),
            ("$note", ids.LimboNoteId),
            ("$t", now));

        // Upcoming meeting linked to Harbor Court for cell indicator.
        var meetingId = Guid.NewGuid().ToString("D");
        Exec(connection, tx,
            """
            INSERT INTO calendar_events (id, calendar_source_id, title, starts_at, ends_at, location, created_at, updated_at)
            VALUES ($id, NULL, 'MetroFiber site survey', $start, $end, 'Harbor Court', $t, $t);
            """,
            ("$id", meetingId),
            ("$start", DateTime.UtcNow.AddDays(1).ToString("O")),
            ("$end", DateTime.UtcNow.AddDays(1).AddHours(1).ToString("O")),
            ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO event_entity_links (id, calendar_event_id, entity_type, entity_id, created_at)
            VALUES ($id, $event, 'project', $project, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$event", meetingId),
            ("$project", ids.HarborProjectId), ("$t", now));

        // "Our" org EIN + W-9 file for Phase 16 evidence AC.
        Exec(connection, tx,
            "INSERT INTO organizations (id, name, kind, notes, created_at, updated_at) VALUES ($id, $name, 'self', 'Operating entity', $t, $t);",
            ("$id", ids.AcmeOrgId), ("$name", "Acme Holdings"), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO contact_fact_provenance (
              id, entity_type, entity_id, field, value, source_email_id, source_kind, created_at)
            VALUES ($id, 'organization', $org, 'ein', $ein, NULL, 'demo_seed', $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$org", ids.AcmeOrgId),
            ("$ein", ids.AcmeEin), ("$t", now));

        var demoDir = Path.Combine(Path.GetDirectoryName(_factory.DatabasePath) ?? Path.GetTempPath(), "demo-seed");
        Directory.CreateDirectory(demoDir);
        var w9Path = Path.Combine(demoDir, "W-9-Acme-Holdings.txt");
        var w9Text =
            $"Form W-9 Request for Taxpayer Identification Number{Environment.NewLine}"
            + $"Name: Acme Holdings{Environment.NewLine}"
            + $"EIN: {ids.AcmeEin}{Environment.NewLine}"
            + "Employer identification number for Acme Holdings.";
        File.WriteAllText(w9Path, w9Text);

        Exec(connection, tx,
            """
            INSERT INTO file_artifacts (
              id, path, display_name, extension, indexed_text, mime_type, size_bytes, created_at, updated_at)
            VALUES ($id, $path, $name, 'txt', $text, 'text/plain', $size, $t, $t);
            """,
            ("$id", ids.W9FileId), ("$path", w9Path), ("$name", "W-9-Acme-Holdings.txt"),
            ("$text", w9Text), ("$size", (long)w9Text.Length), ("$t", now));

        Exec(connection, tx,
            """
            INSERT INTO file_entity_links (id, file_artifact_id, entity_type, entity_id, created_at)
            VALUES ($id, $file, 'organization', $org, $t);
            """,
            ("$id", Guid.NewGuid().ToString("D")), ("$file", ids.W9FileId),
            ("$org", ids.AcmeOrgId), ("$t", now));

        // Indexable under Harbor Court for fragmentary file search demos.
        Exec(connection, tx,
            "INSERT INTO file_project_links (id, file_artifact_id, project_id, created_at) VALUES ($id, $file, $project, $t);",
            ("$id", Guid.NewGuid().ToString("D")), ("$file", ids.W9FileId),
            ("$project", ids.HarborProjectId), ("$t", now));

        tx.Commit();
        return ids;
    }

    private static void Exec(
        SqliteConnection connection,
        SqliteTransaction tx,
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
}

public sealed class DemoGraphIds
{
    public required string MetroFiberOrgId { get; init; }
    public required string AcmeOrgId { get; init; }
    public required string AcmeEin { get; init; }
    public required string W9FileId { get; init; }
    public required string HarborProjectId { get; init; }
    public required string RiverviewProjectId { get; init; }
    public required string HarborInternetWorkstreamId { get; init; }
    public required string RiverviewInternetWorkstreamId { get; init; }
    public required string HarborTaskId { get; init; }
    public required string RiverviewTaskId { get; init; }
    public required string MetroFiberServesHarborRelId { get; init; }
    public required string MetroFiberServesRiverviewRelId { get; init; }
    public required string ContactPersonId { get; init; }
    public required string SharedEmailId { get; init; }
    public required string HarborExtractionId { get; init; }
    public required string RiverviewExtractionId { get; init; }
    public required string LimboNoteId { get; init; }
    public required string LimboSuggestionId { get; init; }

    public static DemoGraphIds CreateNew() => new()
    {
        MetroFiberOrgId = Guid.NewGuid().ToString("D"),
        AcmeOrgId = Guid.NewGuid().ToString("D"),
        AcmeEin = "12-3456789",
        W9FileId = Guid.NewGuid().ToString("D"),
        HarborProjectId = Guid.NewGuid().ToString("D"),
        RiverviewProjectId = Guid.NewGuid().ToString("D"),
        HarborInternetWorkstreamId = Guid.NewGuid().ToString("D"),
        RiverviewInternetWorkstreamId = Guid.NewGuid().ToString("D"),
        HarborTaskId = Guid.NewGuid().ToString("D"),
        RiverviewTaskId = Guid.NewGuid().ToString("D"),
        MetroFiberServesHarborRelId = Guid.NewGuid().ToString("D"),
        MetroFiberServesRiverviewRelId = Guid.NewGuid().ToString("D"),
        ContactPersonId = Guid.NewGuid().ToString("D"),
        SharedEmailId = Guid.NewGuid().ToString("D"),
        HarborExtractionId = Guid.NewGuid().ToString("D"),
        RiverviewExtractionId = Guid.NewGuid().ToString("D"),
        LimboNoteId = Guid.NewGuid().ToString("D"),
        LimboSuggestionId = Guid.NewGuid().ToString("D"),
    };

    public static DemoGraphIds ReadExisting(SqliteConnection connection)
    {
        string ProjectId(string name)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id FROM projects WHERE name = $n LIMIT 1;";
            cmd.Parameters.AddWithValue("$n", name);
            return (string)(cmd.ExecuteScalar() ?? throw new InvalidOperationException($"Missing project {name}"));
        }

        var harbor = ProjectId("Harbor Court");
        var riverview = ProjectId("Riverview");
        string? acmeOrg = null;
        string? w9 = null;
        string ein = "12-3456789";
        try
        {
            acmeOrg = Scalar(connection, "SELECT id FROM organizations WHERE name = 'Acme Holdings' LIMIT 1;");
            w9 = Scalar(connection, "SELECT id FROM file_artifacts WHERE display_name LIKE 'W-9%' LIMIT 1;");
            ein = Scalar(connection, "SELECT value FROM contact_fact_provenance WHERE field = 'ein' LIMIT 1;");
        }
        catch (InvalidOperationException)
        {
            // Older demo DBs may lack Phase 16 seed rows.
        }

        return new DemoGraphIds
        {
            MetroFiberOrgId = Scalar(connection, "SELECT id FROM organizations WHERE name = 'MetroFiber' LIMIT 1;"),
            AcmeOrgId = acmeOrg ?? Guid.Empty.ToString("D"),
            AcmeEin = ein,
            W9FileId = w9 ?? Guid.Empty.ToString("D"),
            HarborProjectId = harbor,
            RiverviewProjectId = riverview,
            HarborInternetWorkstreamId = Scalar(connection, "SELECT id FROM workstreams WHERE project_id = $p LIMIT 1;", ("$p", harbor)),
            RiverviewInternetWorkstreamId = Scalar(connection, "SELECT id FROM workstreams WHERE project_id = $p LIMIT 1;", ("$p", riverview)),
            HarborTaskId = Scalar(connection, "SELECT id FROM tasks WHERE project_id = $p LIMIT 1;", ("$p", harbor)),
            RiverviewTaskId = Scalar(connection, "SELECT id FROM tasks WHERE project_id = $p LIMIT 1;", ("$p", riverview)),
            MetroFiberServesHarborRelId = Scalar(connection, "SELECT id FROM relationships WHERE project_id = $p AND relationship_type = 'serves' LIMIT 1;", ("$p", harbor)),
            MetroFiberServesRiverviewRelId = Scalar(connection, "SELECT id FROM relationships WHERE project_id = $p AND relationship_type = 'serves' LIMIT 1;", ("$p", riverview)),
            ContactPersonId = Scalar(connection, "SELECT id FROM people WHERE display_name = 'Alex Rivera' LIMIT 1;"),
            SharedEmailId = Scalar(connection, "SELECT id FROM email_artifacts LIMIT 1;"),
            HarborExtractionId = Scalar(connection, "SELECT id FROM email_extractions WHERE project_id = $p LIMIT 1;", ("$p", harbor)),
            RiverviewExtractionId = Scalar(connection, "SELECT id FROM email_extractions WHERE project_id = $p LIMIT 1;", ("$p", riverview)),
            LimboNoteId = Scalar(connection, "SELECT id FROM notes WHERE is_limbo = 1 LIMIT 1;"),
            LimboSuggestionId = Scalar(connection, "SELECT id FROM agent_suggestions WHERE note_id IS NOT NULL LIMIT 1;"),
        };
    }

    private static string Scalar(SqliteConnection connection, string sql, params (string, object)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (string)(cmd.ExecuteScalar() ?? throw new InvalidOperationException("Expected scalar."));
    }
}
