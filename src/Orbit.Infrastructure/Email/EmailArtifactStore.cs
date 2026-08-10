using Microsoft.Data.Sqlite;
using Orbit.Infrastructure.Data;

namespace Orbit.Infrastructure.Email;

public sealed class EmailArtifactStore
{
    private readonly SqliteConnectionFactory _factory;

    public EmailArtifactStore(SqliteConnectionFactory factory) => _factory = factory;

    public string? FindIdByInternetMessageId(string internetMessageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(internetMessageId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM email_artifacts
            WHERE internet_message_id = $mid AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$mid", internetMessageId);
        return cmd.ExecuteScalar() as string;
    }

    public string? FindIdByContentHash(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id FROM email_artifacts
            WHERE content_hash = $hash AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        return cmd.ExecuteScalar() as string;
    }

    public EmailArtifactRecord? Get(string emailId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, subject, sent_at, received_at, internet_message_id, conversation_id,
                   body_preview, raw_path, body_text_path, body_html_path, content_hash
            FROM email_artifacts
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var record = MapArtifact(reader);
        reader.Close();
        return record with
        {
            Participants = ListParticipants(connection, emailId),
            ProjectIds = ListProjectIds(connection, emailId),
            Attachments = ListAttachmentsFromDisk(record.RawPath),
        };
    }

    public void UpsertArtifact(EmailArtifactRecord artifact, IReadOnlyList<ParsedEmailParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var now = DateTime.UtcNow.ToString("O");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = tx;
            upsert.CommandText =
                """
                INSERT INTO email_artifacts (
                  id, subject, sent_at, received_at, internet_message_id, conversation_id,
                  body_preview, raw_path, body_text_path, body_html_path, content_hash,
                  created_at, updated_at)
                VALUES (
                  $id, $subject, $sent, $recv, $mid, $conv,
                  $preview, $raw, $bodyTxt, $bodyHtml, $hash,
                  $t, $t)
                ON CONFLICT(id) DO UPDATE SET
                  subject = excluded.subject,
                  sent_at = excluded.sent_at,
                  received_at = excluded.received_at,
                  internet_message_id = excluded.internet_message_id,
                  conversation_id = excluded.conversation_id,
                  body_preview = excluded.body_preview,
                  raw_path = excluded.raw_path,
                  body_text_path = excluded.body_text_path,
                  body_html_path = excluded.body_html_path,
                  content_hash = excluded.content_hash,
                  updated_at = excluded.updated_at,
                  archived_at = NULL;
                """;
            upsert.Parameters.AddWithValue("$id", artifact.Id);
            upsert.Parameters.AddWithValue("$subject", (object?)artifact.Subject ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$sent", (object?)artifact.SentAt ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$recv", (object?)artifact.ReceivedAt ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$mid", (object?)artifact.InternetMessageId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$conv", (object?)artifact.ConversationId ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$preview", (object?)artifact.BodyPreview ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$raw", (object?)artifact.RawPath ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$bodyTxt", (object?)artifact.BodyTextPath ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$bodyHtml", (object?)artifact.BodyHtmlPath ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$hash", (object?)artifact.ContentHash ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$t", now);
            upsert.ExecuteNonQuery();
        }

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM email_participants WHERE email_artifact_id = $id;";
            clear.Parameters.AddWithValue("$id", artifact.Id);
            clear.ExecuteNonQuery();
        }

        foreach (var participant in participants)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText =
                """
                INSERT INTO email_participants (id, email_artifact_id, role, address, display_name, created_at)
                VALUES ($id, $email, $role, $addr, $name, $t);
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$email", artifact.Id);
            insert.Parameters.AddWithValue("$role", participant.Role);
            insert.Parameters.AddWithValue("$addr", participant.Address);
            insert.Parameters.AddWithValue("$name", (object?)participant.DisplayName ?? DBNull.Value);
            insert.Parameters.AddWithValue("$t", now);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void LinkToProject(string emailId, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        EnsureEmailExists(emailId);
        EnsureProjectExists(projectId);

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO email_project_links (id, email_artifact_id, project_id, created_at)
            VALUES ($id, $email, $project, $t)
            ON CONFLICT(email_artifact_id, project_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("$email", emailId);
        cmd.Parameters.AddWithValue("$project", projectId);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    private void EnsureEmailExists(string emailId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM email_artifacts WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", emailId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Email was not found.", nameof(emailId));
        }
    }

    private void EnsureProjectExists(string projectId)
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM projects WHERE id = $id AND archived_at IS NULL LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", projectId);
        if (cmd.ExecuteScalar() is null)
        {
            throw new ArgumentException("Project was not found.", nameof(projectId));
        }
    }

    private static EmailArtifactRecord MapArtifact(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Subject = reader.IsDBNull(1) ? null : reader.GetString(1),
            SentAt = reader.IsDBNull(2) ? null : reader.GetString(2),
            ReceivedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
            InternetMessageId = reader.IsDBNull(4) ? null : reader.GetString(4),
            ConversationId = reader.IsDBNull(5) ? null : reader.GetString(5),
            BodyPreview = reader.IsDBNull(6) ? null : reader.GetString(6),
            RawPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            BodyTextPath = reader.IsDBNull(8) ? null : reader.GetString(8),
            BodyHtmlPath = reader.IsDBNull(9) ? null : reader.GetString(9),
            ContentHash = reader.IsDBNull(10) ? null : reader.GetString(10),
        };

    private static IReadOnlyList<EmailParticipantRecord> ListParticipants(SqliteConnection connection, string emailId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, role, address, display_name
            FROM email_participants
            WHERE email_artifact_id = $id
            ORDER BY role, address COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        var list = new List<EmailParticipantRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new EmailParticipantRecord
            {
                Id = reader.GetString(0),
                Role = reader.GetString(1),
                Address = reader.GetString(2),
                DisplayName = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return list;
    }

    private static IReadOnlyList<string> ListProjectIds(SqliteConnection connection, string emailId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT project_id FROM email_project_links
            WHERE email_artifact_id = $id
            ORDER BY created_at;
            """;
        cmd.Parameters.AddWithValue("$id", emailId);
        var list = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static IReadOnlyList<EmailAttachmentRecord> ListAttachmentsFromDisk(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return [];
        }

        var dir = Path.Combine(Path.GetDirectoryName(rawPath) ?? string.Empty, "attachments");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir)
            .Select(path => new EmailAttachmentRecord
            {
                FileName = Path.GetFileName(path),
                Path = path,
                SizeBytes = new FileInfo(path).Length,
            })
            .OrderBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
