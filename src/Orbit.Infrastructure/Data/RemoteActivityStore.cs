using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Orbit.Infrastructure.Data;

public sealed class RemoteConversationSummary
{
    public required string Id { get; init; }

    public required string Channel { get; init; }

    public string? Title { get; init; }

    public string? HermesSessionId { get; init; }

    public string? ExternalThreadId { get; init; }

    public required string UpdatedAt { get; init; }

    public required string CreatedAt { get; init; }
}

public sealed class RemoteAuditEventSummary
{
    public required string Id { get; init; }

    public required string EventType { get; init; }

    public string? EntityType { get; init; }

    public string? EntityId { get; init; }

    public required string Actor { get; init; }

    public string? DetailJson { get; init; }

    public required string CreatedAt { get; init; }

    public string? Channel { get; init; }

    public string? HermesSessionId { get; init; }

    public string? ExternalUserId { get; init; }

    public string? Summary { get; init; }
}

public sealed class RemoteActivitySnapshot
{
    public required IReadOnlyList<RemoteConversationSummary> Conversations { get; init; }

    public required IReadOnlyList<RemoteAuditEventSummary> AuditEvents { get; init; }
}

/// <summary>
/// Reads telegram-channel conversations and audit rows stamped with telegram provenance.
/// </summary>
public sealed class RemoteActivityStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly SqliteConnectionFactory _factory;

    public RemoteActivityStore(SqliteConnectionFactory factory) => _factory = factory;

    public RemoteActivitySnapshot GetRemoteActivity(int conversationLimit = 20, int auditLimit = 40)
    {
        conversationLimit = Math.Clamp(conversationLimit, 1, 100);
        auditLimit = Math.Clamp(auditLimit, 1, 200);

        using var connection = _factory.CreateConnection();
        return new RemoteActivitySnapshot
        {
            Conversations = ListTelegramConversations(connection, conversationLimit),
            AuditEvents = ListTelegramAudits(connection, auditLimit),
        };
    }

    private static IReadOnlyList<RemoteConversationSummary> ListTelegramConversations(
        SqliteConnection connection,
        int limit)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT id, channel, title, hermes_session_id, external_thread_id, created_at, updated_at
            FROM conversations
            WHERE channel = $channel AND archived_at IS NULL
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$channel", ConversationStore.ChannelTelegram);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RemoteConversationSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RemoteConversationSummary
            {
                Id = reader.GetString(0),
                Channel = reader.GetString(1),
                Title = reader.IsDBNull(2) ? null : reader.GetString(2),
                HermesSessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                ExternalThreadId = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = reader.GetString(5),
                UpdatedAt = reader.GetString(6),
            });
        }

        return list;
    }

    private static IReadOnlyList<RemoteAuditEventSummary> ListTelegramAudits(
        SqliteConnection connection,
        int limit)
    {
        using var cmd = connection.CreateCommand();
        // Prefer JSON extract; LIKE fallback covers older/odd payloads.
        cmd.CommandText =
            """
            SELECT id, event_type, entity_type, entity_id, actor, detail_json, created_at
            FROM audit_events
            WHERE
              lower(coalesce(json_extract(detail_json, '$.provenance.channel'), '')) = 'telegram'
              OR lower(coalesce(json_extract(detail_json, '$.platformProvenance.channel'), '')) = 'telegram'
              OR lower(coalesce(json_extract(detail_json, '$.channel'), '')) = 'telegram'
              OR detail_json LIKE '%"channel":"telegram"%'
              OR detail_json LIKE '%"channel": "telegram"%'
            ORDER BY created_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RemoteAuditEventSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var detailJson = reader.IsDBNull(5) ? null : reader.GetString(5);
            var parsed = ParseProvenance(detailJson);
            list.Add(new RemoteAuditEventSummary
            {
                Id = reader.GetString(0),
                EventType = reader.GetString(1),
                EntityType = reader.IsDBNull(2) ? null : reader.GetString(2),
                EntityId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Actor = reader.GetString(4),
                DetailJson = detailJson,
                CreatedAt = reader.GetString(6),
                Channel = parsed.Channel,
                HermesSessionId = parsed.HermesSessionId,
                ExternalUserId = parsed.ExternalUserId,
                Summary = BuildSummary(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), detailJson),
            });
        }

        return list;
    }

    private static (string? Channel, string? HermesSessionId, string? ExternalUserId) ParseProvenance(string? detailJson)
    {
        if (string.IsNullOrWhiteSpace(detailJson))
        {
            return (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;
            if (TryReadProvenanceObject(root, "provenance", out var p)
                || TryReadProvenanceObject(root, "platformProvenance", out p))
            {
                return (
                    ReadString(p, "channel"),
                    ReadString(p, "hermesSessionId"),
                    ReadString(p, "externalUserId"));
            }

            return (
                ReadString(root, "channel"),
                ReadString(root, "hermesSessionId"),
                ReadString(root, "externalUserId"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static bool TryReadProvenanceObject(JsonElement root, string name, out JsonElement obj)
    {
        obj = default;
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        obj = node;
        return true;
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string BuildSummary(string eventType, string? entityType, string? detailJson)
    {
        var bits = new List<string> { eventType };
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            bits.Add(entityType);
        }

        if (!string.IsNullOrWhiteSpace(detailJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(detailJson);
                if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    bits.Add(title.GetString() ?? string.Empty);
                }
                else if (doc.RootElement.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.String)
                {
                    bits.Add(summary.GetString() ?? string.Empty);
                }
            }
            catch (JsonException)
            {
                // ignore
            }
        }

        return string.Join(" · ", bits.Where(b => !string.IsNullOrWhiteSpace(b)));
    }
}
