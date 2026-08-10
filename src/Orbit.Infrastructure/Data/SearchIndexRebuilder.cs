using Microsoft.Data.Sqlite;
using Orbit.Core.Data;

namespace Orbit.Infrastructure.Data;

public sealed class SearchIndexRebuilder
{
    private readonly SqliteConnectionFactory _factory;

    public SearchIndexRebuilder(SqliteConnectionFactory factory) => _factory = factory;

    public int Rebuild()
    {
        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        Execute(connection, tx, "DELETE FROM search_documents;");
        try
        {
            Execute(connection, tx, "INSERT INTO search_documents_fts(search_documents_fts) VALUES('rebuild');");
        }
        catch (SqliteException)
        {
            // FTS may be empty on first rebuild; continue with content insert + rebuild after.
        }

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'project', id, id, name, COALESCE(summary, ''), updated_at FROM projects WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'workstream', id, project_id, name,
                   trim(COALESCE(status, '') || ' ' || COALESCE(next_action, '')),
                   updated_at
            FROM workstreams WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'task', id, project_id, title, COALESCE(body, ''), updated_at FROM tasks WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'blocker', id, project_id, substr(summary, 1, 80), summary, updated_at
            FROM blockers WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'note', id, project_id, substr(original_text, 1, 80), original_text, updated_at FROM notes WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'person', id, NULL, display_name, COALESCE(notes, ''), updated_at FROM people WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT id, 'organization', id, NULL, name, COALESCE(notes, ''), updated_at FROM organizations WHERE archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT e.id, 'email', e.id,
                   (SELECT epl.project_id FROM email_project_links epl
                    WHERE epl.email_artifact_id = e.id ORDER BY epl.created_at LIMIT 1),
                   COALESCE(e.subject, '(no subject)'),
                   COALESCE(e.body_preview, ''),
                   e.updated_at
            FROM email_artifacts e
            WHERE e.archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT fa.id, 'file', fa.id,
                   (SELECT fpl.project_id FROM file_project_links fpl
                    WHERE fpl.file_artifact_id = fa.id ORDER BY fpl.created_at LIMIT 1),
                   COALESCE(fa.display_name, fa.path),
                   COALESCE(fa.indexed_text, ''),
                   fa.updated_at
            FROM file_artifacts fa
            WHERE fa.archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT e.id, 'calendar_event', e.id,
                   (SELECT eel.entity_id FROM event_entity_links eel
                    WHERE eel.calendar_event_id = e.id AND eel.entity_type = 'project'
                    ORDER BY eel.created_at LIMIT 1),
                   e.title,
                   trim(COALESCE(e.location, '') || ' ' || COALESCE(e.body_preview, '') || ' ' || COALESCE(e.organizer, '')),
                   e.updated_at
            FROM calendar_events e
            WHERE e.archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT c.id, 'conversation', c.id, NULL,
                   COALESCE(c.title, c.channel || ' conversation'),
                   COALESCE((
                     SELECT group_concat(m.body, ' ')
                     FROM (
                       SELECT body FROM conversation_messages
                       WHERE conversation_id = c.id
                       ORDER BY sent_at DESC
                       LIMIT 12
                     ) m
                   ), ''),
                   c.updated_at
            FROM conversations c
            WHERE c.archived_at IS NULL;
            """);

        Execute(
            connection,
            tx,
            """
            INSERT INTO search_documents (id, entity_type, entity_id, project_id, title, body, updated_at)
            SELECT m.id, 'message', m.id, NULL,
                   substr(m.body, 1, 80),
                   m.body,
                   m.created_at
            FROM conversation_messages m;
            """);

        try
        {
            Execute(connection, tx, "INSERT INTO search_documents_fts(search_documents_fts) VALUES('rebuild');");
        }
        catch (SqliteException)
        {
            // If FTS5 unavailable, projection table alone remains rebuildable.
        }

        tx.Commit();

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM search_documents;";
        return Convert.ToInt32(count.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
