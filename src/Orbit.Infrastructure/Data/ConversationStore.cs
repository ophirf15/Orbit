using Microsoft.Data.Sqlite;

namespace Orbit.Infrastructure.Data;

public sealed class ConversationRecord
{
    public required string Id { get; init; }

    public required string Channel { get; init; }

    public string? Title { get; init; }

    public string? ExternalThreadId { get; init; }

    public string? HermesSessionId { get; init; }

    public string? HermesSessionKey { get; init; }

    public required string CreatedAt { get; init; }

    public required string UpdatedAt { get; init; }
}

public sealed class ConversationMessageRecord
{
    public required string Id { get; init; }

    public required string ConversationId { get; init; }

    public required string Role { get; init; }

    public required string Body { get; init; }

    public required string SentAt { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class ConversationStore
{
    public const string ChannelDesktop = "desktop";

    public const string ChannelTelegram = "telegram";

    private static readonly HashSet<string> AllowedChannels = new(StringComparer.OrdinalIgnoreCase)
    {
        ChannelDesktop,
        ChannelTelegram,
    };

    private readonly SqliteConnectionFactory _factory;

    public ConversationStore(SqliteConnectionFactory factory) => _factory = factory;

    public static string NormalizeChannel(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var trimmed = channel.Trim().ToLowerInvariant();
        if (!AllowedChannels.Contains(trimmed))
        {
            throw new ArgumentException(
                $"Unknown channel '{channel}'. Allowed: {ChannelDesktop}, {ChannelTelegram}.",
                nameof(channel));
        }

        return trimmed;
    }

    public ConversationRecord CreateConversation(
        string channel,
        string? title = null,
        string? hermesSessionId = null,
        string? hermesSessionKey = null,
        string? externalThreadId = null)
    {
        var normalizedChannel = NormalizeChannel(channel);

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO conversations (
              id, channel, title, external_thread_id, hermes_session_id, hermes_session_key, created_at, updated_at)
            VALUES (
              $id, $channel, $title, $ext, $hid, $hkey, $t, $t);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$channel", normalizedChannel);
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ext", (object?)externalThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hid", (object?)hermesSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hkey", (object?)hermesSessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();

        return new ConversationRecord
        {
            Id = id,
            Channel = normalizedChannel,
            Title = title,
            ExternalThreadId = externalThreadId,
            HermesSessionId = hermesSessionId,
            HermesSessionKey = hermesSessionKey,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Upserts a conversation mirror for Hermes (desktop or telegram). Matches by
    /// <paramref name="conversationId"/> or <c>hermesSessionId</c>, otherwise inserts.
    /// </summary>
    public ConversationRecord SyncConversation(
        string channel,
        string? hermesSessionId = null,
        string? hermesSessionKey = null,
        string? title = null,
        string? externalThreadId = null,
        string? conversationId = null)
    {
        var normalizedChannel = NormalizeChannel(channel);
        if (string.IsNullOrWhiteSpace(hermesSessionId) && string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("hermesSessionId or conversationId is required.");
        }

        ConversationRecord? existing = null;
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            existing = Get(conversationId);
            if (existing is null)
            {
                throw new ArgumentException("Conversation was not found.", nameof(conversationId));
            }
        }
        else if (!string.IsNullOrWhiteSpace(hermesSessionId))
        {
            existing = FindByHermesSessionId(hermesSessionId);
        }

        if (existing is null)
        {
            return CreateConversation(
                normalizedChannel,
                title ?? (normalizedChannel == ChannelTelegram ? "Telegram" : "Agent chat"),
                hermesSessionId,
                hermesSessionKey,
                externalThreadId ?? hermesSessionId);
        }

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE conversations
            SET channel = $channel,
                title = COALESCE($title, title),
                external_thread_id = COALESCE($ext, external_thread_id),
                hermes_session_id = COALESCE($hid, hermes_session_id),
                hermes_session_key = COALESCE($hkey, hermes_session_key),
                updated_at = $t
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", existing.Id);
        cmd.Parameters.AddWithValue("$channel", normalizedChannel);
        cmd.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(title) ? DBNull.Value : title.Trim());
        cmd.Parameters.AddWithValue(
            "$ext",
            string.IsNullOrWhiteSpace(externalThreadId) ? DBNull.Value : externalThreadId.Trim());
        cmd.Parameters.AddWithValue(
            "$hid",
            string.IsNullOrWhiteSpace(hermesSessionId) ? DBNull.Value : hermesSessionId.Trim());
        cmd.Parameters.AddWithValue(
            "$hkey",
            string.IsNullOrWhiteSpace(hermesSessionKey) ? DBNull.Value : hermesSessionKey.Trim());
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();

        return Get(existing.Id)
            ?? throw new InvalidOperationException("Conversation was not readable after sync.");
    }

    public IReadOnlyList<ConversationRecord> ListByChannel(string channel, int limit = 20)
    {
        var normalizedChannel = NormalizeChannel(channel);
        limit = Math.Clamp(limit, 1, 100);

        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, channel, title, external_thread_id, hermes_session_id, hermes_session_key, created_at, updated_at
            FROM conversations
            WHERE channel = $c AND archived_at IS NULL
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$c", normalizedChannel);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ConversationRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadConversation(reader));
        }

        return list;
    }

    public ConversationRecord? Get(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, channel, title, external_thread_id, hermes_session_id, hermes_session_key, created_at, updated_at
            FROM conversations
            WHERE id = $id AND archived_at IS NULL
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public ConversationRecord? FindByHermesSessionId(string hermesSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hermesSessionId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, channel, title, external_thread_id, hermes_session_id, hermes_session_key, created_at, updated_at
            FROM conversations
            WHERE hermes_session_id = $hid AND archived_at IS NULL
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$hid", hermesSessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public ConversationRecord? GetLatestDesktop()
    {
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, channel, title, external_thread_id, hermes_session_id, hermes_session_key, created_at, updated_at
            FROM conversations
            WHERE channel = $c AND archived_at IS NULL
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$c", ChannelDesktop);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadConversation(reader) : null;
    }

    public void BindHermesSession(string conversationId, string hermesSessionId, string? hermesSessionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hermesSessionId);

        var now = DateTime.UtcNow.ToString("O");
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            UPDATE conversations
            SET hermes_session_id = $hid,
                hermes_session_key = $hkey,
                external_thread_id = COALESCE(external_thread_id, $hid),
                updated_at = $t
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", conversationId);
        cmd.Parameters.AddWithValue("$hid", hermesSessionId);
        cmd.Parameters.AddWithValue("$hkey", (object?)hermesSessionKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates a new desktop conversation or resumes the latest one, ensuring a Hermes session id is bound.
    /// </summary>
    public ConversationRecord CreateOrResumeDesktop(
        string hermesSessionId,
        string? hermesSessionKey,
        string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hermesSessionId);

        var existing = FindByHermesSessionId(hermesSessionId) ?? GetLatestDesktop();
        if (existing is not null)
        {
            if (!string.Equals(existing.HermesSessionId, hermesSessionId, StringComparison.Ordinal))
            {
                BindHermesSession(existing.Id, hermesSessionId, hermesSessionKey);
                return Get(existing.Id)!;
            }

            if (hermesSessionKey is not null &&
                !string.Equals(existing.HermesSessionKey, hermesSessionKey, StringComparison.Ordinal))
            {
                BindHermesSession(existing.Id, hermesSessionId, hermesSessionKey);
                return Get(existing.Id)!;
            }

            return existing;
        }

        return CreateConversation(
            ChannelDesktop,
            title ?? "Agent chat",
            hermesSessionId,
            hermesSessionKey,
            externalThreadId: hermesSessionId);
    }

    public ConversationMessageRecord AppendMessage(string conversationId, string role, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        var now = DateTime.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("D");

        using var connection = _factory.CreateConnection();
        using var tx = connection.BeginTransaction();

        using (var msg = connection.CreateCommand())
        {
            msg.Transaction = tx;
            msg.CommandText =
                """
                INSERT INTO conversation_messages (id, conversation_id, role, body, sent_at, created_at)
                VALUES ($id, $cid, $role, $body, $t, $t);
                """;
            msg.Parameters.AddWithValue("$id", id);
            msg.Parameters.AddWithValue("$cid", conversationId);
            msg.Parameters.AddWithValue("$role", role.Trim());
            msg.Parameters.AddWithValue("$body", body);
            msg.Parameters.AddWithValue("$t", now);
            msg.ExecuteNonQuery();
        }

        using (var upd = connection.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = "UPDATE conversations SET updated_at = $t WHERE id = $id;";
            upd.Parameters.AddWithValue("$t", now);
            upd.Parameters.AddWithValue("$id", conversationId);
            upd.ExecuteNonQuery();
        }

        tx.Commit();

        return new ConversationMessageRecord
        {
            Id = id,
            ConversationId = conversationId,
            Role = role.Trim(),
            Body = body,
            SentAt = now,
            CreatedAt = now,
        };
    }

    public IReadOnlyList<ConversationMessageRecord> ListMessages(string conversationId, int limit = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        using var connection = _factory.CreateConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, conversation_id, role, body, sent_at, created_at
            FROM conversation_messages
            WHERE conversation_id = $cid
            ORDER BY sent_at ASC, created_at ASC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));

        var list = new List<ConversationMessageRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ConversationMessageRecord
            {
                Id = reader.GetString(0),
                ConversationId = reader.GetString(1),
                Role = reader.GetString(2),
                Body = reader.GetString(3),
                SentAt = reader.GetString(4),
                CreatedAt = reader.GetString(5),
            });
        }

        return list;
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Channel = reader.GetString(1),
            Title = reader.IsDBNull(2) ? null : reader.GetString(2),
            ExternalThreadId = reader.IsDBNull(3) ? null : reader.GetString(3),
            HermesSessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
            HermesSessionKey = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = reader.GetString(6),
            UpdatedAt = reader.GetString(7),
        };
}
